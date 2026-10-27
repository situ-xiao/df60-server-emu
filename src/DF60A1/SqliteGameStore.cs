using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Text;

namespace DF60A1
{
    internal sealed class SqliteGameStore : IDisposable
    {
        // This emulator intentionally starts every local test character with
        // enough Cera to exercise the cash-shop transaction.  Purchases still
        // debit and persist the exact balance.
        internal const int InitialLocalCash = 100000;

        private readonly string _connectionString;
        private readonly object _gate = new object();

        public SqliteGameStore(string databasePath, string schemaPath)
        {
            var directory = Path.GetDirectoryName(databasePath);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            _connectionString = "Data Source=" + databasePath + ";Version=3;Foreign Keys=True;Journal Mode=WAL;";

            using (var connection = Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = File.ReadAllText(schemaPath, Encoding.UTF8);
                command.ExecuteNonQuery();
            }

            // 迁移：已存在的旧数据库没有 exp 列（CREATE TABLE IF NOT EXISTS
            // 不会修改既有表）。ALTER 在列已存在时会抛 SQLiteException，忽略即可。
            using (var connection = Open())
            using (var migrate = connection.CreateCommand())
            {
                migrate.CommandText = "ALTER TABLE characters ADD COLUMN exp INTEGER NOT NULL DEFAULT 0;";
                try { migrate.ExecuteNonQuery(); }
                catch (SQLiteException) { }
                migrate.CommandText = "ALTER TABLE characters ADD COLUMN inventory_initialized INTEGER NOT NULL DEFAULT 0;";
                try { migrate.ExecuteNonQuery(); }
                catch (SQLiteException) { }
                migrate.CommandText = "ALTER TABLE characters ADD COLUMN skill_points INTEGER NOT NULL DEFAULT 0;";
                try { migrate.ExecuteNonQuery(); }
                catch (SQLiteException) { }
                // SP earned from experience and SP books is persistent.  Do not
                // clamp values earned from experience or SP books.
                // A legacy schema used 9999 as the column default.  Repair only
                // untouched level-one characters that were created with that
                // sentinel, without changing SP earned by existing characters.
                migrate.CommandText = "UPDATE characters SET skill_points=0,updated_at=CURRENT_TIMESTAMP WHERE level=1 AND exp=0 AND skill_points=9999;";
                migrate.ExecuteNonQuery();
                migrate.CommandText = "ALTER TABLE characters ADD COLUMN gold INTEGER NOT NULL DEFAULT 99999;";
                try { migrate.ExecuteNonQuery(); }
                catch (SQLiteException) { }
                migrate.CommandText = "ALTER TABLE characters ADD COLUMN cash INTEGER NOT NULL DEFAULT 0;";
                try { migrate.ExecuteNonQuery(); }
                catch (SQLiteException) { }
                // Old databases have no way to distinguish an untouched zero
                // cash balance from one legitimately spent to zero.  Add an
                // explicit one-time marker, grant the local test balance once,
                // then never refill that character again.
                migrate.CommandText = "ALTER TABLE characters ADD COLUMN cash_initialized INTEGER NOT NULL DEFAULT 0;";
                try { migrate.ExecuteNonQuery(); }
                catch (SQLiteException) { }
                migrate.CommandText = "UPDATE characters SET cash=@cash,cash_initialized=1,updated_at=CURRENT_TIMESTAMP WHERE cash_initialized=0;";
                migrate.Parameters.Clear();
                migrate.Parameters.AddWithValue("@cash", InitialLocalCash);
                migrate.ExecuteNonQuery();
                var addedTutorialColumn = false;
                migrate.CommandText = "ALTER TABLE characters ADD COLUMN tutorial_started INTEGER NOT NULL DEFAULT 0;";
                try { migrate.ExecuteNonQuery(); addedTutorialColumn = true; }
                catch (SQLiteException) { }
                // Characters from databases created before tutorial persistence was
                // introduced have already passed their first entry.  Only those rows
                // are migrated to completed; newly created characters start at zero.
                if (addedTutorialColumn)
                {
                    migrate.CommandText = "UPDATE characters SET tutorial_started=1;";
                    migrate.ExecuteNonQuery();
                }
                foreach (var column in new[]
                {
                    "bonus_strength", "bonus_intelligence", "bonus_vitality", "bonus_spirit",
                    "bonus_maximum_hp", "bonus_maximum_mp", "bonus_movement_speed", "bonus_all_resistance"
                })
                {
                    migrate.CommandText = "ALTER TABLE characters ADD COLUMN " + column + " INTEGER NOT NULL DEFAULT 0;";
                    try { migrate.ExecuteNonQuery(); }
                    catch (SQLiteException) { }
                }
            }
        }

        public int EnsureAccount(string accountName, string password)
        {
            accountName = string.IsNullOrWhiteSpace(accountName) ? "test" : accountName.Trim();
            lock (_gate)
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT OR IGNORE INTO accounts(account_name,password) VALUES(@name,@password);";
                    insert.Parameters.AddWithValue("@name", accountName);
                    insert.Parameters.AddWithValue("@password", password ?? string.Empty);
                    insert.ExecuteNonQuery();
                }
                int id;
                using (var select = connection.CreateCommand())
                {
                    select.Transaction = transaction;
                    select.CommandText = "SELECT account_id FROM accounts WHERE account_name=@name;";
                    select.Parameters.AddWithValue("@name", accountName);
                    id = Convert.ToInt32(select.ExecuteScalar());
                }
                using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE accounts SET last_login_at=CURRENT_TIMESTAMP WHERE account_id=@id;";
                    update.Parameters.AddWithValue("@id", id);
                    update.ExecuteNonQuery();
                }
                transaction.Commit();
                return id;
            }
        }

        public byte[] LoadClientConfig(int accountId)
        {
            if (accountId <= 0) return null;
            lock (_gate)
            using (var connection = Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT payload FROM account_client_config WHERE account_id=@account;";
                command.Parameters.AddWithValue("@account", accountId);
                var value = command.ExecuteScalar();
                if (value == null || value == DBNull.Value) return null;
                var payload = value as byte[];
                return payload == null ? null : (byte[])payload.Clone();
            }
        }

        public void SaveClientConfig(int accountId, byte[] payload)
        {
            if (accountId <= 0 || payload == null || payload.Length == 0) return;
            lock (_gate)
            using (var connection = Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "INSERT OR REPLACE INTO account_client_config(account_id,payload,updated_at) " +
                    "VALUES(@account,@payload,CURRENT_TIMESTAMP);";
                command.Parameters.AddWithValue("@account", accountId);
                command.Parameters.Add("@payload", System.Data.DbType.Binary, payload.Length).Value = payload;
                command.ExecuteNonQuery();
            }
        }

        public int MergeLocalAccountCharacters(string sourceAccountName, int targetAccountId)
        {
            if (string.IsNullOrWhiteSpace(sourceAccountName) || targetAccountId <= 0) return 0;
            lock (_gate)
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                int sourceAccountId;
                using (var find = connection.CreateCommand())
                {
                    find.Transaction = transaction;
                    find.CommandText = "SELECT account_id FROM accounts WHERE account_name=@name COLLATE NOCASE;";
                    find.Parameters.AddWithValue("@name", sourceAccountName.Trim());
                    var value = find.ExecuteScalar();
                    if (value == null || value == DBNull.Value) return 0;
                    sourceAccountId = Convert.ToInt32(value);
                }
                if (sourceAccountId == targetAccountId) return 0;

                int nextSlot;
                using (var slots = connection.CreateCommand())
                {
                    slots.Transaction = transaction;
                    slots.CommandText = "SELECT COALESCE(MAX(slot_index),-1)+1 FROM characters WHERE account_id=@id;";
                    slots.Parameters.AddWithValue("@id", targetAccountId);
                    nextSlot = Convert.ToInt32(slots.ExecuteScalar());
                }

                var characterIds = new List<int>();
                using (var list = connection.CreateCommand())
                {
                    list.Transaction = transaction;
                    list.CommandText = "SELECT character_id FROM characters WHERE account_id=@id ORDER BY slot_index,character_id;";
                    list.Parameters.AddWithValue("@id", sourceAccountId);
                    using (var reader = list.ExecuteReader())
                        while (reader.Read()) characterIds.Add(reader.GetInt32(0));
                }
                if (nextSlot + characterIds.Count > 16) return 0;

                for (var index = 0; index < characterIds.Count; index++)
                {
                    using (var move = connection.CreateCommand())
                    {
                        move.Transaction = transaction;
                        move.CommandText = "UPDATE characters SET account_id=@target,slot_index=@slot,updated_at=CURRENT_TIMESTAMP WHERE character_id=@character;";
                        move.Parameters.AddWithValue("@target", targetAccountId);
                        move.Parameters.AddWithValue("@slot", nextSlot + index);
                        move.Parameters.AddWithValue("@character", characterIds[index]);
                        move.ExecuteNonQuery();
                    }
                }
                using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM accounts WHERE account_id=@source AND NOT EXISTS(SELECT 1 FROM characters WHERE account_id=@source);";
                    delete.Parameters.AddWithValue("@source", sourceAccountId);
                    delete.ExecuteNonQuery();
                }
                transaction.Commit();
                return characterIds.Count;
            }
        }

        public List<CharacterRecord> ListCharacters(int accountId)
        {
            var result = new List<CharacterRecord>();
            lock (_gate)
            using (var connection = Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT character_id,account_id,slot_index,name,job,grow_type,level,exp,skill_points,gold,cash,town_id,area_id,pos_x,pos_y,direction,tutorial_started,bonus_strength,bonus_intelligence,bonus_vitality,bonus_spirit,bonus_maximum_hp,bonus_maximum_mp,bonus_movement_speed,bonus_all_resistance FROM characters WHERE account_id=@id ORDER BY slot_index;";
                command.Parameters.AddWithValue("@id", accountId);
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) result.Add(Map(reader));
            }

            // Repair progression written by older builds that treated the
            // cumulative PVF thresholds as per-level requirements. This also
            // makes an already-full experience bar level up on the roster,
            // without requiring the player to kill one more monster.
            foreach (var character in result)
            {
                var requirement = ExperienceTable.ExpRequiredWithinLevel(character.Level);
                if (requirement <= 0 || character.Exp < requirement) continue;
                var normalized = GrantExperience(character.Id, 0);
                if (normalized == null) continue;
                character.Level = (byte)normalized.Item2;
                character.Exp = normalized.Item3;
            }
            return result;
        }

        public CreateCharacterResult CreateCharacter(int accountId, string name, byte job)
        {
            name = (name ?? string.Empty).Trim();
            var encodedLength = Encoding.GetEncoding(936).GetByteCount(name);
            if (encodedLength < 1 || encodedLength > 18) return CreateCharacterResult.Fail("角色名必须为 1-18 个 GBK 字节");
            // DNF.exe 1.0.1.9 create UI exposes five jobs.  Value 5 is the
            // client's internal "no selection" sentinel, not a valid job.
            if (job > 4) return CreateCharacterResult.Fail("职业编号无效（Arad客户端只接受 0-4）");

            lock (_gate)
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                int slot;
                using (var slotCommand = connection.CreateCommand())
                {
                    slotCommand.Transaction = transaction;
                    slotCommand.CommandText = "SELECT COALESCE(MAX(slot_index),-1)+1 FROM characters WHERE account_id=@id;";
                    slotCommand.Parameters.AddWithValue("@id", accountId);
                    slot = Convert.ToInt32(slotCommand.ExecuteScalar());
                }
                if (slot >= 16) return CreateCharacterResult.Fail("角色槽已满");

                try
                {
                    long id;
                    using (var insert = connection.CreateCommand())
                    {
                        insert.Transaction = transaction;
                        // Spell out the initial town position instead of
                        // relying on an already-created database's column
                        // defaults.  In this client's Script.pvf, town 1 area
                        // 1 is Elvengard/Gate.map.  The map's only
                        // Gate.map's only [town movable area] is
                        // (330,324,289,24), whose centre is (474,336).
                        // ARAD initially reports the transition marker (474,234),
                        // but that point is not on the room's walkable floor.
                        // Explicitly persist the starting SP.  Some upgraded
                        // databases still have the legacy 9999 column default,
                        // and CREATE TABLE IF NOT EXISTS cannot replace it.
                        insert.CommandText = "INSERT INTO characters(account_id,slot_index,name,job,skill_points,cash,cash_initialized,tutorial_started,town_id,area_id,pos_x,pos_y,direction) VALUES(@account,@slot,@name,@job,0,@cash,1,0,1,1,474,336,5); SELECT last_insert_rowid();";
                        insert.Parameters.AddWithValue("@account", accountId);
                        insert.Parameters.AddWithValue("@slot", slot);
                        insert.Parameters.AddWithValue("@name", name);
                        insert.Parameters.AddWithValue("@job", job);
                        insert.Parameters.AddWithValue("@cash", InitialLocalCash);
                        id = (long)insert.ExecuteScalar();
                    }
                    foreach (var skill in GamePackets.InitialSkills(job))
                    {
                        using (var insertSkill = connection.CreateCommand())
                        {
                            insertSkill.Transaction = transaction;
                            insertSkill.CommandText = "INSERT INTO character_skills(character_id,slot_index,skill_id,skill_level) VALUES(@id,@slot,@skill,@level);";
                            insertSkill.Parameters.AddWithValue("@id", id);
                            insertSkill.Parameters.AddWithValue("@slot", skill.Slot);
                            insertSkill.Parameters.AddWithValue("@skill", skill.SkillId);
                            insertSkill.Parameters.AddWithValue("@level", skill.Level);
                            insertSkill.ExecuteNonQuery();
                        }
                    }
                    transaction.Commit();
                    return new CreateCharacterResult
                    {
                        Success = true,
                        Character = new CharacterRecord { Id = (int)id, AccountId = accountId, Slot = (byte)slot, Name = name, Job = job, GrowType = 0, Level = 1, Exp = 0, SkillPoints = 0, Gold = 99999, Cash = InitialLocalCash, TutorialStarted = false, TownId = 1, AreaId = 1, X = 474, Y = 336, Direction = 5 }
                    };
                }
                catch (SQLiteException exception)
                {
                    transaction.Rollback();
                    return CreateCharacterResult.Fail(exception.ResultCode == SQLiteErrorCode.Constraint ? "角色名已存在" : exception.Message);
                }
            }
        }

        public void UpdatePosition(int characterId, byte town, byte area, short x, short y, byte direction)
        {
            lock (_gate)
            using (var connection = Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE characters SET town_id=@town,area_id=@area,pos_x=@x,pos_y=@y,direction=@direction,updated_at=CURRENT_TIMESTAMP WHERE character_id=@id;";
                command.Parameters.AddWithValue("@town", town);
                command.Parameters.AddWithValue("@area", area);
                command.Parameters.AddWithValue("@x", x);
                command.Parameters.AddWithValue("@y", y);
                command.Parameters.AddWithValue("@direction", direction);
                command.Parameters.AddWithValue("@id", characterId);
                command.ExecuteNonQuery();
            }
        }

        public List<InventoryItemRecord> LoadInventory(int characterId)
        {
            var result = new List<InventoryItemRecord>();
            lock (_gate)
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                var initialized = 0;
                using (var selectCharacter = connection.CreateCommand())
                {
                    selectCharacter.Transaction = transaction;
                    selectCharacter.CommandText = "SELECT inventory_initialized FROM characters WHERE character_id=@id;";
                    selectCharacter.Parameters.AddWithValue("@id", characterId);
                    var value = selectCharacter.ExecuteScalar();
                    if (value == null || value == DBNull.Value) return result;
                    initialized = Convert.ToInt32(value);
                }

                if (initialized == 0)
                {
                    using (var seed = connection.CreateCommand())
                    {
                        seed.Transaction = transaction;
                        seed.CommandText = "UPDATE characters SET inventory_initialized=1 WHERE character_id=@id;";
                        seed.Parameters.AddWithValue("@id", characterId);
                        seed.ExecuteNonQuery();
                    }
                }

                // Remove the old starter-stack artifact which occupied the
                // first visible backpack cell. Restrict the migration to the
                // exact seeded row so genuine item-8 stacks are preserved.
                using (var removeStarter = connection.CreateCommand())
                {
                    removeStarter.Transaction = transaction;
                    removeStarter.CommandText = "DELETE FROM character_items WHERE character_id=@id AND slot_index=39 AND item_id=8 AND item_count=100 AND item_state=0 AND durability=0 AND seal_state=0;";
                    removeStarter.Parameters.AddWithValue("@id", characterId);
                    removeStarter.ExecuteNonQuery();
                }

                using (var select = connection.CreateCommand())
                {
                    select.Transaction = transaction;
                    select.CommandText = "SELECT slot_index,item_id,item_count,item_state,durability,seal_state FROM character_items WHERE character_id=@id ORDER BY slot_index;";
                    select.Parameters.AddWithValue("@id", characterId);
                    using (var reader = select.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            result.Add(new InventoryItemRecord
                            {
                                Slot = (ushort)reader.GetInt32(0), ItemId = (ushort)reader.GetInt32(1),
                                Count = (uint)reader.GetInt64(2), State = (byte)reader.GetInt32(3),
                                Durability = (ushort)reader.GetInt32(4), SealState = (byte)reader.GetInt32(5)
                            });
                        }
                    }
                }
                transaction.Commit();
            }
            return result;
        }

        public void SaveInventory(int characterId, IEnumerable<InventoryItemRecord> items)
        {
            var snapshot = new List<InventoryItemRecord>(items ?? new InventoryItemRecord[0]);
            lock (_gate)
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM character_items WHERE character_id=@id;";
                    delete.Parameters.AddWithValue("@id", characterId);
                    delete.ExecuteNonQuery();
                }
                foreach (var item in snapshot)
                {
                    if (item == null || item.Slot < 3 || item.ItemId == 0 || item.Count == 0) continue;
                    using (var insert = connection.CreateCommand())
                    {
                        insert.Transaction = transaction;
                        insert.CommandText = "INSERT INTO character_items(character_id,slot_index,item_id,item_count,item_state,durability,seal_state) VALUES(@id,@slot,@item,@count,@state,@durability,@seal);";
                        insert.Parameters.AddWithValue("@id", characterId);
                        insert.Parameters.AddWithValue("@slot", item.Slot);
                        insert.Parameters.AddWithValue("@item", item.ItemId);
                        insert.Parameters.AddWithValue("@count", item.Count);
                        insert.Parameters.AddWithValue("@state", item.State);
                        insert.Parameters.AddWithValue("@durability", item.Durability);
                        insert.Parameters.AddWithValue("@seal", item.SealState);
                        insert.ExecuteNonQuery();
                    }
                }
                using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE characters SET inventory_initialized=1,updated_at=CURRENT_TIMESTAMP WHERE character_id=@id;";
                    update.Parameters.AddWithValue("@id", characterId);
                    update.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        public void SaveInventoryAndEquipment(int characterId, IEnumerable<InventoryItemRecord> inventory,
            IEnumerable<InventoryItemRecord> equipment)
        {
            var inventorySnapshot = new List<InventoryItemRecord>(inventory ?? new InventoryItemRecord[0]);
            var equipmentSnapshot = new List<InventoryItemRecord>(equipment ?? new InventoryItemRecord[0]);
            lock (_gate)
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                ReplaceItems(connection, transaction, "character_items", characterId,
                    inventorySnapshot, 3, ushort.MaxValue);
                ReplaceItems(connection, transaction, "character_equipment", characterId,
                    equipmentSnapshot, 0, 0x17);
                using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE characters SET inventory_initialized=1,updated_at=CURRENT_TIMESTAMP WHERE character_id=@id;";
                    update.Parameters.AddWithValue("@id", characterId);
                    update.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        public List<InventoryItemRecord> LoadStorage(int characterId)
        {
            var result = new List<InventoryItemRecord>();
            lock (_gate)
            using (var connection = Open())
            using (var select = connection.CreateCommand())
            {
                select.CommandText = "SELECT slot_index,item_id,item_count,item_state,durability,seal_state FROM character_storage WHERE character_id=@id ORDER BY slot_index;";
                select.Parameters.AddWithValue("@id", characterId);
                using (var reader = select.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add(new InventoryItemRecord
                        {
                            Slot = (ushort)reader.GetInt32(0), ItemId = (ushort)reader.GetInt32(1),
                            Count = (uint)reader.GetInt64(2), State = (byte)reader.GetInt32(3),
                            Durability = (ushort)reader.GetInt32(4), SealState = (byte)reader.GetInt32(5)
                        });
                    }
                }
            }
            return result;
        }

        public void SaveStorage(int characterId, IEnumerable<InventoryItemRecord> items)
        {
            SaveInventoryAndStorage(characterId, null, items);
        }

        public void SaveInventoryAndStorage(int characterId, IEnumerable<InventoryItemRecord> inventory,
            IEnumerable<InventoryItemRecord> storage)
        {
            var inventorySnapshot = inventory == null ? null : new List<InventoryItemRecord>(inventory);
            var storageSnapshot = storage == null ? null : new List<InventoryItemRecord>(storage);
            lock (_gate)
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                if (inventorySnapshot != null)
                    ReplaceItems(connection, transaction, "character_items", characterId, inventorySnapshot, 3, ushort.MaxValue);
                if (storageSnapshot != null)
                    ReplaceItems(connection, transaction, "character_storage", characterId, storageSnapshot, 0, 120);
                using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE characters SET inventory_initialized=1,updated_at=CURRENT_TIMESTAMP WHERE character_id=@id;";
                    update.Parameters.AddWithValue("@id", characterId);
                    update.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        private static void ReplaceItems(SQLiteConnection connection, SQLiteTransaction transaction, string table,
            int characterId, IEnumerable<InventoryItemRecord> items, ushort minimumSlot, int exclusiveMaximumSlot)
        {
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM " + table + " WHERE character_id=@id;";
                delete.Parameters.AddWithValue("@id", characterId);
                delete.ExecuteNonQuery();
            }
            foreach (var item in items)
            {
                if (item == null || item.Slot < minimumSlot || item.Slot >= exclusiveMaximumSlot ||
                    item.ItemId == 0 || item.Count == 0) continue;
                using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT INTO " + table + "(character_id,slot_index,item_id,item_count,item_state,durability,seal_state) VALUES(@id,@slot,@item,@count,@state,@durability,@seal);";
                    insert.Parameters.AddWithValue("@id", characterId);
                    insert.Parameters.AddWithValue("@slot", item.Slot);
                    insert.Parameters.AddWithValue("@item", item.ItemId);
                    insert.Parameters.AddWithValue("@count", item.Count);
                    insert.Parameters.AddWithValue("@state", item.State);
                    insert.Parameters.AddWithValue("@durability", item.Durability);
                    insert.Parameters.AddWithValue("@seal", item.SealState);
                    insert.ExecuteNonQuery();
                }
            }
        }

        public void SaveTownConsumableState(CharacterRecord character, IEnumerable<InventoryItemRecord> items)
        {
            if (character == null) throw new ArgumentNullException("character");
            var snapshot = new List<InventoryItemRecord>(items ?? new InventoryItemRecord[0]);
            lock (_gate)
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM character_items WHERE character_id=@id;";
                    delete.Parameters.AddWithValue("@id", character.Id);
                    delete.ExecuteNonQuery();
                }
                foreach (var item in snapshot)
                {
                    if (item == null || item.Slot < 3 || item.ItemId == 0 || item.Count == 0) continue;
                    using (var insert = connection.CreateCommand())
                    {
                        insert.Transaction = transaction;
                        insert.CommandText = "INSERT INTO character_items(character_id,slot_index,item_id,item_count,item_state,durability,seal_state) VALUES(@id,@slot,@item,@count,@state,@durability,@seal);";
                        insert.Parameters.AddWithValue("@id", character.Id);
                        insert.Parameters.AddWithValue("@slot", item.Slot);
                        insert.Parameters.AddWithValue("@item", item.ItemId);
                        insert.Parameters.AddWithValue("@count", item.Count);
                        insert.Parameters.AddWithValue("@state", item.State);
                        insert.Parameters.AddWithValue("@durability", item.Durability);
                        insert.Parameters.AddWithValue("@seal", item.SealState);
                        insert.ExecuteNonQuery();
                    }
                }
                using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE characters SET level=@level,exp=@exp,skill_points=@sp,bonus_strength=@str,bonus_intelligence=@int,bonus_vitality=@vit,bonus_spirit=@spr,bonus_maximum_hp=@hp,bonus_maximum_mp=@mp,bonus_movement_speed=@speed,bonus_all_resistance=@resist,inventory_initialized=1,updated_at=CURRENT_TIMESTAMP WHERE character_id=@id;";
                    update.Parameters.AddWithValue("@level", character.Level);
                    update.Parameters.AddWithValue("@exp", Math.Max(0, character.Exp));
                    update.Parameters.AddWithValue("@sp", Math.Max(0, character.SkillPoints));
                    update.Parameters.AddWithValue("@str", Math.Max(0, character.BonusStrength));
                    update.Parameters.AddWithValue("@int", Math.Max(0, character.BonusIntelligence));
                    update.Parameters.AddWithValue("@vit", Math.Max(0, character.BonusVitality));
                    update.Parameters.AddWithValue("@spr", Math.Max(0, character.BonusSpirit));
                    update.Parameters.AddWithValue("@hp", Math.Max(0, character.BonusMaximumHp));
                    update.Parameters.AddWithValue("@mp", Math.Max(0, character.BonusMaximumMp));
                    update.Parameters.AddWithValue("@speed", Math.Max(0, character.BonusMovementSpeed));
                    update.Parameters.AddWithValue("@resist", Math.Max(0, character.BonusAllResistance));
                    update.Parameters.AddWithValue("@id", character.Id);
                    update.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        public void SaveGoldAndInventory(int characterId, int gold, IEnumerable<InventoryItemRecord> items)
        {
            var snapshot = new List<InventoryItemRecord>(items ?? new InventoryItemRecord[0]);
            lock (_gate)
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM character_items WHERE character_id=@id;";
                    delete.Parameters.AddWithValue("@id", characterId);
                    delete.ExecuteNonQuery();
                }
                foreach (var item in snapshot)
                {
                    if (item == null || item.Slot < 3 || item.ItemId == 0 || item.Count == 0) continue;
                    using (var insert = connection.CreateCommand())
                    {
                        insert.Transaction = transaction;
                        insert.CommandText = "INSERT INTO character_items(character_id,slot_index,item_id,item_count,item_state,durability,seal_state) VALUES(@id,@slot,@item,@count,@state,@durability,@seal);";
                        insert.Parameters.AddWithValue("@id", characterId);
                        insert.Parameters.AddWithValue("@slot", item.Slot);
                        insert.Parameters.AddWithValue("@item", item.ItemId);
                        insert.Parameters.AddWithValue("@count", item.Count);
                        insert.Parameters.AddWithValue("@state", item.State);
                        insert.Parameters.AddWithValue("@durability", item.Durability);
                        insert.Parameters.AddWithValue("@seal", item.SealState);
                        insert.ExecuteNonQuery();
                    }
                }
                using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE characters SET gold=@gold,inventory_initialized=1,updated_at=CURRENT_TIMESTAMP WHERE character_id=@id;";
                    update.Parameters.AddWithValue("@gold", Math.Max(0, gold));
                    update.Parameters.AddWithValue("@id", characterId);
                    update.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        public void SaveCash(int characterId, int cash)
        {
            lock (_gate)
            using (var connection = Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE characters SET cash=@cash,cash_initialized=1,updated_at=CURRENT_TIMESTAMP WHERE character_id=@id;";
                command.Parameters.AddWithValue("@cash", Math.Max(0, cash));
                command.Parameters.AddWithValue("@id", characterId);
                command.ExecuteNonQuery();
            }
        }

        public void SaveCashInventoryAndAvatars(int characterId, int cash,
            IEnumerable<InventoryItemRecord> inventory, IEnumerable<InventoryItemRecord> avatars)
        {
            var inventorySnapshot = new List<InventoryItemRecord>(
                inventory ?? new InventoryItemRecord[0]);
            var avatarSnapshot = new List<InventoryItemRecord>(
                avatars ?? new InventoryItemRecord[0]);
            lock (_gate)
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                ReplaceItems(connection, transaction, "character_items", characterId,
                    inventorySnapshot, 3, ushort.MaxValue);
                ReplaceAvatarItems(connection, transaction, characterId, avatarSnapshot);
                using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE characters SET cash=@cash,cash_initialized=1,inventory_initialized=1," +
                        "updated_at=CURRENT_TIMESTAMP WHERE character_id=@id;";
                    update.Parameters.AddWithValue("@cash", Math.Max(0, cash));
                    update.Parameters.AddWithValue("@id", characterId);
                    update.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        public List<InventoryItemRecord> LoadEquipment(int characterId, byte job)
        {
            var result = new List<InventoryItemRecord>();
            lock (_gate)
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                using (var count = connection.CreateCommand())
                {
                    count.Transaction = transaction;
                    count.CommandText = "SELECT COUNT(*) FROM character_equipment WHERE character_id=@id;";
                    count.Parameters.AddWithValue("@id", characterId);
                    if (Convert.ToInt32(count.ExecuteScalar()) == 0)
                    {
                        using (var seed = connection.CreateCommand())
                        {
                            seed.Transaction = transaction;
                            seed.CommandText = "INSERT INTO character_equipment(character_id,slot_index,item_id,item_count,item_state,durability,seal_state) VALUES(@id,9,@item,1,0,35,0);";
                            seed.Parameters.AddWithValue("@id", characterId);
                            seed.Parameters.AddWithValue("@item", GamePackets.InitialWeaponId(job));
                            seed.ExecuteNonQuery();
                        }
                    }
                }
                using (var select = connection.CreateCommand())
                {
                    select.Transaction = transaction;
                    select.CommandText = "SELECT slot_index,item_id,item_count,item_state,durability,seal_state FROM character_equipment WHERE character_id=@id ORDER BY slot_index;";
                    select.Parameters.AddWithValue("@id", characterId);
                    using (var reader = select.ExecuteReader())
                        while (reader.Read()) result.Add(ReadItem(reader));
                }
                transaction.Commit();
            }
            return result;
        }

        public void SaveEquipment(int characterId, IEnumerable<InventoryItemRecord> items)
        {
            var snapshot = new List<InventoryItemRecord>(items ?? new InventoryItemRecord[0]);
            lock (_gate)
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM character_equipment WHERE character_id=@id;";
                    delete.Parameters.AddWithValue("@id", characterId);
                    delete.ExecuteNonQuery();
                }
                foreach (var item in snapshot)
                {
                    if (item == null || item.ItemId == 0 || item.Count == 0) continue;
                    using (var insert = connection.CreateCommand())
                    {
                        insert.Transaction = transaction;
                        insert.CommandText = "INSERT INTO character_equipment(character_id,slot_index,item_id,item_count,item_state,durability,seal_state) VALUES(@id,@slot,@item,@count,@state,@durability,@seal);";
                        insert.Parameters.AddWithValue("@id", characterId);
                        insert.Parameters.AddWithValue("@slot", item.Slot);
                        insert.Parameters.AddWithValue("@item", item.ItemId);
                        insert.Parameters.AddWithValue("@count", item.Count);
                        insert.Parameters.AddWithValue("@state", item.State);
                        insert.Parameters.AddWithValue("@durability", item.Durability);
                        insert.Parameters.AddWithValue("@seal", item.SealState);
                        insert.ExecuteNonQuery();
                    }
                }
                transaction.Commit();
            }
        }

        public List<InventoryItemRecord> LoadAvatars(int characterId)
        {
            var result = new List<InventoryItemRecord>();
            lock (_gate)
            using (var connection = Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT slot_index,item_id,item_count,item_state,durability,seal_state FROM character_avatars WHERE character_id=@id ORDER BY slot_index;";
                command.Parameters.AddWithValue("@id", characterId);
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) result.Add(ReadItem(reader));
            }
            return result;
        }

        public void SaveAvatars(int characterId, IEnumerable<InventoryItemRecord> items)
        {
            var snapshot = new List<InventoryItemRecord>(items ?? new InventoryItemRecord[0]);
            lock (_gate)
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                ReplaceAvatarItems(connection, transaction, characterId, snapshot);
                transaction.Commit();
            }
        }

        private static void ReplaceAvatarItems(SQLiteConnection connection,
            SQLiteTransaction transaction, int characterId,
            IEnumerable<InventoryItemRecord> items)
        {
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM character_avatars WHERE character_id=@id;";
                delete.Parameters.AddWithValue("@id", characterId);
                delete.ExecuteNonQuery();
            }
            foreach (var item in items ?? Enumerable.Empty<InventoryItemRecord>())
            {
                // Avatar expiration 0 is the permanent sentinel, not an empty row.
                if (item == null || item.ItemId == 0) continue;
                using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT INTO character_avatars(character_id,slot_index,item_id,item_count,item_state,durability,seal_state) VALUES(@id,@slot,@item,@count,@state,@durability,@seal);";
                    insert.Parameters.AddWithValue("@id", characterId);
                    insert.Parameters.AddWithValue("@slot", item.Slot);
                    insert.Parameters.AddWithValue("@item", item.ItemId);
                    insert.Parameters.AddWithValue("@count", item.Count);
                    insert.Parameters.AddWithValue("@state", item.State);
                    insert.Parameters.AddWithValue("@durability", item.Durability);
                    insert.Parameters.AddWithValue("@seal", item.SealState);
                    insert.ExecuteNonQuery();
                }
            }
        }

        public List<CharacterSkillRecord> LoadSkills(int characterId)
        {
            var result = new List<CharacterSkillRecord>();
            lock (_gate)
            using (var connection = Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT slot_index,skill_id,skill_level FROM character_skills WHERE character_id=@id ORDER BY slot_index;";
                command.Parameters.AddWithValue("@id", characterId);
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) result.Add(new CharacterSkillRecord
                    {
                        Slot = (byte)reader.GetInt32(0), SkillId = (byte)reader.GetInt32(1), Level = (byte)reader.GetInt32(2)
                    });
            }
            return result;
        }

        public void SaveSkills(int characterId, int skillPoints, IEnumerable<CharacterSkillRecord> skills)
        {
            var snapshot = RepairDuplicateSkillSlots(skills);
            lock (_gate)
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM character_skills WHERE character_id=@id;";
                    delete.Parameters.AddWithValue("@id", characterId);
                    delete.ExecuteNonQuery();
                }
                foreach (var skill in snapshot)
                {
                    using (var insert = connection.CreateCommand())
                    {
                        insert.Transaction = transaction;
                        insert.CommandText = "INSERT INTO character_skills(character_id,slot_index,skill_id,skill_level) VALUES(@id,@slot,@skill,@level);";
                        insert.Parameters.AddWithValue("@id", characterId);
                        insert.Parameters.AddWithValue("@slot", skill.Slot);
                        insert.Parameters.AddWithValue("@skill", skill.SkillId);
                        insert.Parameters.AddWithValue("@level", skill.Level);
                        insert.ExecuteNonQuery();
                    }
                }
                using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE characters SET skill_points=@sp,updated_at=CURRENT_TIMESTAMP WHERE character_id=@id;";
                    update.Parameters.AddWithValue("@sp", Math.Max(0, skillPoints));
                    update.Parameters.AddWithValue("@id", characterId);
                    update.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        private static List<CharacterSkillRecord> RepairDuplicateSkillSlots(IEnumerable<CharacterSkillRecord> skills)
        {
            var snapshot = (skills ?? Enumerable.Empty<CharacterSkillRecord>())
                .Where(value => value != null && value.Level > 0)
                .GroupBy(value => value.SkillId)
                .Select(group => group.OrderByDescending(value => value.Level).First())
                .OrderBy(value => value.Slot)
                .Select(value => new CharacterSkillRecord
                {
                    Slot = value.Slot,
                    SkillId = value.SkillId,
                    Level = value.Level
                }).ToList();
            var occupied = new HashSet<byte>();
            foreach (var skill in snapshot)
            {
                if (occupied.Add(skill.Slot)) continue;
                var replacement = byte.MaxValue;
                for (var slot = 6; slot <= 215; slot++)
                    if (!occupied.Contains((byte)slot)) { replacement = (byte)slot; break; }
                if (replacement == byte.MaxValue)
                    throw new InvalidOperationException("技能栏没有可用于修复重复槽位的空位。");
                skill.Slot = replacement;
                occupied.Add(replacement);
            }
            return snapshot.OrderBy(value => value.Slot).ToList();
        }

        public bool CompleteTutorial(int characterId)
        {
            lock (_gate)
            using (var connection = Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE characters SET tutorial_started=1,updated_at=CURRENT_TIMESTAMP WHERE character_id=@id AND tutorial_started=0;";
                command.Parameters.AddWithValue("@id", characterId);
                return command.ExecuteNonQuery() == 1;
            }
        }

        // Kept for callers compiled against the older, misleading name.  The
        // persisted flag means that the tutorial has been completed, not that
        // it has merely begun.
        public bool TryBeginTutorial(int characterId)
        {
            return CompleteTutorial(characterId);
        }

        // 结算怪物死亡经验：服务端权威累加 exp，并按升级阈值推进等级。
        // 阈值表与客户端 character/exptable.tbl 一致（1->2 级需要 450）。
        // 返回 (是否升级, 新等级, 当前 exp, 升级所需 exp)；角色不存在时返回 null。
        public Tuple<bool, int, int, int, int> GrantExperience(int characterId, int amount)
        {
            lock (_gate)
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                int level, exp, skillPoints;
                using (var select = connection.CreateCommand())
                {
                    select.Transaction = transaction;
                    select.CommandText = "SELECT level,exp,skill_points FROM characters WHERE character_id=@id;";
                    select.Parameters.AddWithValue("@id", characterId);
                    using (var reader = select.ExecuteReader())
                    {
                        if (!reader.Read()) return null;
                        level = reader.GetInt32(0);
                        exp = reader.GetInt32(1);
                        skillPoints = reader.GetInt32(2);
                    }
                }
                bool leveled;
                var skillPointsGained = ExperienceTable.ApplyExperience(ref level, ref exp,
                    amount, out leveled);
                skillPoints += skillPointsGained;
                using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE characters SET level=@level,exp=@exp,skill_points=@sp,updated_at=CURRENT_TIMESTAMP WHERE character_id=@id;";
                    update.Parameters.AddWithValue("@level", level);
                    update.Parameters.AddWithValue("@exp", exp);
                    update.Parameters.AddWithValue("@sp", skillPoints);
                    update.Parameters.AddWithValue("@id", characterId);
                    update.ExecuteNonQuery();
                }
                transaction.Commit();
                return Tuple.Create(leveled, level, exp, ExperienceTable.ExpRequiredWithinLevel(level), skillPoints);
            }
        }

        public List<CharacterQuestRecord> LoadActiveQuests(int characterId)
        {
            var result = new List<CharacterQuestRecord>();
            lock (_gate)
            using (var connection = Open())
            using (var command = connection.CreateCommand())
            {
                // The client displays a limited notification list, but the
                // character's active quest collection is not limited to the
                // first three rows.  LIMIT 3 made every quest after the
                // third disappear again on reload.
                command.CommandText = "SELECT quest_id,trigger_value FROM character_quests WHERE character_id=@id ORDER BY quest_id;";
                command.Parameters.AddWithValue("@id", characterId);
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) result.Add(new CharacterQuestRecord
                    {
                        QuestId = (ushort)reader.GetInt32(0), Trigger = (byte)reader.GetInt32(1)
                    });
            }
            return result;
        }

        public HashSet<ushort> LoadCompletedQuests(int characterId)
        {
            var result = new HashSet<ushort>();
            lock (_gate)
            using (var connection = Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT quest_id FROM character_completed_quests WHERE character_id=@id;";
                command.Parameters.AddWithValue("@id", characterId);
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) result.Add((ushort)reader.GetInt32(0));
            }
            return result;
        }

        public Dictionary<ushort, byte> LoadDungeonProgress(int characterId)
        {
            var result = new Dictionary<ushort, byte>();
            lock (_gate)
            using (var connection = Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT dungeon_id,max_difficulty FROM character_dungeon_progress WHERE character_id=@id ORDER BY dungeon_id;";
                command.Parameters.AddWithValue("@id", characterId);
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                        result[(ushort)reader.GetInt32(0)] = (byte)Math.Min(3, Math.Max(0, reader.GetInt32(1)));
            }
            return result;
        }

        public byte UnlockNextDungeonDifficulty(int characterId, ushort dungeonId, byte clearedDifficulty)
        {
            var unlockedDifficulty = (byte)Math.Min(3, clearedDifficulty + 1);
            lock (_gate)
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT OR IGNORE INTO character_dungeon_progress(character_id,dungeon_id,max_difficulty) VALUES(@id,@dungeon,@difficulty);";
                    insert.Parameters.AddWithValue("@id", characterId);
                    insert.Parameters.AddWithValue("@dungeon", dungeonId);
                    insert.Parameters.AddWithValue("@difficulty", unlockedDifficulty);
                    insert.ExecuteNonQuery();
                }
                using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE character_dungeon_progress SET max_difficulty=MAX(max_difficulty,@difficulty) WHERE character_id=@id AND dungeon_id=@dungeon;";
                    update.Parameters.AddWithValue("@difficulty", unlockedDifficulty);
                    update.Parameters.AddWithValue("@id", characterId);
                    update.Parameters.AddWithValue("@dungeon", dungeonId);
                    update.ExecuteNonQuery();
                }
                byte result;
                using (var select = connection.CreateCommand())
                {
                    select.Transaction = transaction;
                    select.CommandText = "SELECT max_difficulty FROM character_dungeon_progress WHERE character_id=@id AND dungeon_id=@dungeon;";
                    select.Parameters.AddWithValue("@id", characterId);
                    select.Parameters.AddWithValue("@dungeon", dungeonId);
                    result = (byte)Math.Min(3, Math.Max(0, Convert.ToInt32(select.ExecuteScalar())));
                }
                transaction.Commit();
                return result;
            }
        }

        public void SaveActiveQuest(int characterId, CharacterQuestRecord quest)
        {
            lock (_gate)
            using (var connection = Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT OR REPLACE INTO character_quests(character_id,quest_id,trigger_value) VALUES(@id,@quest,@trigger);";
                command.Parameters.AddWithValue("@id", characterId);
                command.Parameters.AddWithValue("@quest", quest.QuestId);
                command.Parameters.AddWithValue("@trigger", quest.Trigger);
                command.ExecuteNonQuery();
            }
        }

        public void SaveAcceptedQuestAndInventory(int characterId, CharacterQuestRecord quest,
            IEnumerable<InventoryItemRecord> inventory)
        {
            if (quest == null) throw new ArgumentNullException("quest");
            var inventorySnapshot = new List<InventoryItemRecord>(inventory ?? new InventoryItemRecord[0]);
            lock (_gate)
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "INSERT OR REPLACE INTO character_quests(character_id,quest_id,trigger_value) VALUES(@id,@quest,@trigger);";
                    command.Parameters.AddWithValue("@id", characterId);
                    command.Parameters.AddWithValue("@quest", quest.QuestId);
                    command.Parameters.AddWithValue("@trigger", quest.Trigger);
                    command.ExecuteNonQuery();
                }
                ReplaceItems(connection, transaction, "character_items", characterId,
                    inventorySnapshot, 3, ushort.MaxValue);
                using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE characters SET inventory_initialized=1,updated_at=CURRENT_TIMESTAMP WHERE character_id=@id;";
                    update.Parameters.AddWithValue("@id", characterId);
                    update.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        public void GiveupQuest(int characterId, ushort questId)
        {
            lock (_gate)
            using (var connection = Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM character_quests WHERE character_id=@id AND quest_id=@quest;";
                command.Parameters.AddWithValue("@id", characterId);
                command.Parameters.AddWithValue("@quest", questId);
                command.ExecuteNonQuery();
            }
        }

        public void CompleteQuest(int characterId, ushort questId)
        {
            lock (_gate)
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM character_quests WHERE character_id=@id AND quest_id=@quest;";
                    delete.Parameters.AddWithValue("@id", characterId);
                    delete.Parameters.AddWithValue("@quest", questId);
                    delete.ExecuteNonQuery();
                }
                using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT OR IGNORE INTO character_completed_quests(character_id,quest_id) VALUES(@id,@quest);";
                    insert.Parameters.AddWithValue("@id", characterId);
                    insert.Parameters.AddWithValue("@quest", questId);
                    insert.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        private SQLiteConnection Open()
        {
            var connection = new SQLiteConnection(_connectionString);
            connection.Open();
            return connection;
        }

        private static CharacterRecord Map(SQLiteDataReader reader)
        {
            return new CharacterRecord
            {
                Id = reader.GetInt32(0), AccountId = reader.GetInt32(1), Slot = (byte)reader.GetInt32(2), Name = reader.GetString(3),
                Job = (byte)reader.GetInt32(4), GrowType = (byte)reader.GetInt32(5), Level = (byte)reader.GetInt32(6),
                Exp = reader.GetInt32(7), SkillPoints = reader.GetInt32(8), Gold = reader.GetInt32(9), Cash = reader.GetInt32(10),
                TownId = (byte)reader.GetInt32(11), AreaId = (byte)reader.GetInt32(12), X = (short)reader.GetInt32(13),
                Y = (short)reader.GetInt32(14), Direction = (byte)reader.GetInt32(15),
                TutorialStarted = reader.GetInt32(16) != 0,
                BonusStrength = reader.GetInt32(17), BonusIntelligence = reader.GetInt32(18),
                BonusVitality = reader.GetInt32(19), BonusSpirit = reader.GetInt32(20),
                BonusMaximumHp = reader.GetInt32(21), BonusMaximumMp = reader.GetInt32(22),
                BonusMovementSpeed = reader.GetInt32(23), BonusAllResistance = reader.GetInt32(24)
            };
        }

        private static InventoryItemRecord ReadItem(SQLiteDataReader reader)
        {
            return new InventoryItemRecord
            {
                Slot = (ushort)reader.GetInt32(0), ItemId = (ushort)reader.GetInt32(1), Count = (uint)reader.GetInt64(2),
                State = (byte)reader.GetInt32(3), Durability = (ushort)reader.GetInt32(4), SealState = (byte)reader.GetInt32(5)
            };
        }

        public void Dispose() { }
    }
}
