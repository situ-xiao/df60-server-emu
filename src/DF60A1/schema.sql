PRAGMA foreign_keys = ON;
PRAGMA journal_mode = WAL;

CREATE TABLE IF NOT EXISTS accounts (
    account_id INTEGER PRIMARY KEY AUTOINCREMENT,
    account_name TEXT NOT NULL UNIQUE COLLATE NOCASE,
    password TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    last_login_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS characters (
    character_id INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id INTEGER NOT NULL,
    slot_index INTEGER NOT NULL,
    name TEXT NOT NULL UNIQUE COLLATE NOCASE,
    job INTEGER NOT NULL,
    grow_type INTEGER NOT NULL DEFAULT 0,
    level INTEGER NOT NULL DEFAULT 1,
    exp INTEGER NOT NULL DEFAULT 0,
    skill_points INTEGER NOT NULL DEFAULT 0,
    gold INTEGER NOT NULL DEFAULT 99999,
    -- This is a local emulator test balance, not a real payment balance.
    -- cash_initialized prevents an existing character who has spent all cash
    -- from being refilled when the server restarts.
    cash INTEGER NOT NULL DEFAULT 100000,
    cash_initialized INTEGER NOT NULL DEFAULT 1,
    inventory_initialized INTEGER NOT NULL DEFAULT 0,
    tutorial_started INTEGER NOT NULL DEFAULT 0,
    town_id INTEGER NOT NULL DEFAULT 1,
    -- Town 1 area 1 is Elvengard/Gate.map (the Seria room).
    area_id INTEGER NOT NULL DEFAULT 1,
    pos_x INTEGER NOT NULL DEFAULT 474,
    -- Centre of Gate.map's [town movable area] (330,324,289,24).
    pos_y INTEGER NOT NULL DEFAULT 336,
    direction INTEGER NOT NULL DEFAULT 5,
    bonus_strength INTEGER NOT NULL DEFAULT 0,
    bonus_intelligence INTEGER NOT NULL DEFAULT 0,
    bonus_vitality INTEGER NOT NULL DEFAULT 0,
    bonus_spirit INTEGER NOT NULL DEFAULT 0,
    bonus_maximum_hp INTEGER NOT NULL DEFAULT 0,
    bonus_maximum_mp INTEGER NOT NULL DEFAULT 0,
    bonus_movement_speed INTEGER NOT NULL DEFAULT 0,
    bonus_all_resistance INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY(account_id) REFERENCES accounts(account_id) ON DELETE CASCADE,
    UNIQUE(account_id, slot_index)
);

CREATE INDEX IF NOT EXISTS ix_characters_account_slot
    ON characters(account_id, slot_index);

CREATE TABLE IF NOT EXISTS character_items (
    character_id INTEGER NOT NULL,
    slot_index INTEGER NOT NULL,
    item_id INTEGER NOT NULL,
    item_count INTEGER NOT NULL DEFAULT 1,
    item_state INTEGER NOT NULL DEFAULT 0,
    durability INTEGER NOT NULL DEFAULT 0,
    seal_state INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(character_id, slot_index),
    FOREIGN KEY(character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS character_storage (
    character_id INTEGER NOT NULL,
    slot_index INTEGER NOT NULL,
    item_id INTEGER NOT NULL,
    item_count INTEGER NOT NULL DEFAULT 1,
    item_state INTEGER NOT NULL DEFAULT 0,
    durability INTEGER NOT NULL DEFAULT 0,
    seal_state INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(character_id, slot_index),
    FOREIGN KEY(character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS character_equipment (
    character_id INTEGER NOT NULL,
    slot_index INTEGER NOT NULL,
    item_id INTEGER NOT NULL,
    item_count INTEGER NOT NULL DEFAULT 1,
    item_state INTEGER NOT NULL DEFAULT 0,
    durability INTEGER NOT NULL DEFAULT 0,
    seal_state INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(character_id, slot_index),
    FOREIGN KEY(character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS character_avatars (
    character_id INTEGER NOT NULL,
    slot_index INTEGER NOT NULL,
    item_id INTEGER NOT NULL,
    item_count INTEGER NOT NULL DEFAULT 1,
    item_state INTEGER NOT NULL DEFAULT 0,
    durability INTEGER NOT NULL DEFAULT 0,
    seal_state INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(character_id, slot_index),
    FOREIGN KEY(character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS character_skills (
    character_id INTEGER NOT NULL,
    slot_index INTEGER NOT NULL,
    skill_id INTEGER NOT NULL,
    skill_level INTEGER NOT NULL DEFAULT 1,
    PRIMARY KEY(character_id, skill_id),
    UNIQUE(character_id, slot_index),
    FOREIGN KEY(character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS character_quests (
    character_id INTEGER NOT NULL,
    quest_id INTEGER NOT NULL,
    trigger_value INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(character_id, quest_id),
    FOREIGN KEY(character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS character_completed_quests (
    character_id INTEGER NOT NULL,
    quest_id INTEGER NOT NULL,
    completed_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY(character_id, quest_id),
    FOREIGN KEY(character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS character_dungeon_progress (
    character_id INTEGER NOT NULL,
    dungeon_id INTEGER NOT NULL,
    max_difficulty INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(character_id, dungeon_id),
    FOREIGN KEY(character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

-- Opaque ARAD client preferences (key bindings, audio and other UI options).
-- The client owns the binary layout; keeping the payload intact lets us
-- preserve settings across server restarts without losing unknown fields.
CREATE TABLE IF NOT EXISTS account_client_config (
    account_id INTEGER PRIMARY KEY,
    payload BLOB NOT NULL,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY(account_id) REFERENCES accounts(account_id) ON DELETE CASCADE
);

-- Migrate the old value taken from Elvengard.twn's [gate] return point.
-- That point belongs to area 0 and is outside area 1's movable rectangle.
UPDATE characters
SET pos_y = 336, updated_at = CURRENT_TIMESTAMP
WHERE town_id = 1 AND area_id = 1 AND pos_x = 474 AND pos_y = 234;

-- Builds before 2026-08-01 mislabeled Elvengard area 0 as the Seria room.
UPDATE characters
SET area_id = 1, pos_x = 474, pos_y = 336, updated_at = CURRENT_TIMESTAMP
WHERE town_id = 1 AND area_id = 0
  AND ((pos_x = 474 AND pos_y = 234) OR (pos_x = 405 AND pos_y = 255));
