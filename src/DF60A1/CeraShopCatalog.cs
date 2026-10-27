using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DF60A1
{
    internal sealed class CeraShopProduct
    {
        public uint CommodityNo;
        public ushort ItemId;
        public uint Quantity;
        public int CashPrice;
        public int AvatarDurationType;
        public byte DefaultAvatarOption;
        public string Section;
        public bool IsAvatar { get { return string.Equals(Section, "avatar", StringComparison.OrdinalIgnoreCase); } }
        public int ClientCategory { get { return IsAvatar ? 0 : string.Equals(Section, "coin", StringComparison.OrdinalIgnoreCase) ? 1 : 2; } }
    }

    internal sealed class CeraShopCatalog
    {
        private readonly Dictionary<uint, CeraShopProduct> _products = new Dictionary<uint, CeraShopProduct>();

        public CeraShopCatalog(PvfArchiveReader archive)
        {
            if (archive == null) return;
            try
            {
                uint ignored;
                var text = Encoding.GetEncoding(949).GetString(archive.ReadAllBytes("etc/newcashshop.etc", out ignored)).Replace("\0", string.Empty);
                Parse(text, _products);
            }
            catch { }
        }

        public int Count { get { return _products.Count; } }
        internal IEnumerable<CeraShopProduct> Products
        {
            get { return _products.Values.OrderBy(product => product.CommodityNo); }
        }
        public bool TryGet(uint commodityNo, out CeraShopProduct product) { return _products.TryGetValue(commodityNo, out product); }

        internal static void Parse(string text, IDictionary<uint, CeraShopProduct> products)
        {
            var section = string.Empty;
            foreach (var raw in (text ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var line = StripComment(raw).Trim();
                if (line.Length == 0) continue;
                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                    if (section.StartsWith("/")) section = string.Empty;
                    continue;
                }
                CeraShopProduct product;
                if (TryParseProduct(section, line, out product)) products[product.CommodityNo] = product;
            }
        }

        private static bool TryParseProduct(string section, string line, out CeraShopProduct product)
        {
            product = null;
            var quote = line.IndexOf('`');
            var values = (quote >= 0 ? line.Substring(0, quote) : line)
                .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            uint commodity; ushort itemId;
            if (values.Length < 2 || !uint.TryParse(values[0], out commodity) || !ushort.TryParse(values[1], out itemId) || commodity == 0 || itemId == 0)
                return false;
            if (section == "avatar")
            {
                int durationType;
                if (values.Length < 3 || !int.TryParse(values[2], out durationType)) return false;
                product = new CeraShopProduct
                {
                    CommodityNo = commodity, ItemId = itemId, Quantity = 1,
                    // The Arad avatar table stores duration variants but the
                    // displayed cash price comes from the client-side item data.
                    CashPrice = 350, AvatarDurationType = durationType,
                    DefaultAvatarOption = 0, Section = section
                };
                return true;
            }
            if (section != "item" && section != "coin" && section != "creature" && section != "material" &&
                section != "premium" && section != "recoveryitem" && section != "visual" && section != "package") return false;
            var package = section == "package";
            if (values.Length < (package ? 5 : 6)) return false;
            uint quantity = 1; int price;
            if (!package && (!uint.TryParse(values[2], out quantity) || quantity == 0)) quantity = 1;
            if (!int.TryParse(package ? values[4] : values[5], out price) || price < 0) return false;
            product = new CeraShopProduct { CommodityNo = commodity, ItemId = itemId, Quantity = quantity, CashPrice = price, Section = section };
            return true;
        }

        private static string StripComment(string line)
        {
            var quoted = false;
            for (var index = 0; index + 1 < (line ?? string.Empty).Length; index++)
            {
                if (line[index] == '`') quoted = !quoted;
                else if (!quoted && line[index] == '/' && line[index + 1] == '/') return line.Substring(0, index);
            }
            return line ?? string.Empty;
        }
    }
}
