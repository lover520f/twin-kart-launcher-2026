using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace TwinKartLauncher.Gateway
{
    // Explicit externally measured packed fields, captures and effect tables.
    // No hard-coded vendor performance values or proprietary assets.
    public sealed class NativeKartParameters
    {
        readonly Dictionary<string, object> data;
        readonly byte[] stockBody;
        readonly SteamKartTuning steamTuning;
        static readonly Dictionary<string,object> Neutral=new Dictionary<string,object>();
        public NativeKartParameters(string catalogJson,byte[] vehicleDefaults=null,SteamKartTuning steamTuning=null)
        {
            if((catalogJson==null)==(vehicleDefaults==null))throw new ArgumentException("Exactly one vehicle parameter source is required.");
            data=catalogJson==null ? null : ProfileJson.Parse(catalogJson);
            this.steamTuning=steamTuning;
            if(vehicleDefaults!=null) {
                stockBody=PackClientBody(vehicleDefaults);
                if(BitConverter.ToSingle(vehicleDefaults,0x40)<=0 || BitConverter.ToSingle(vehicleDefaults,0x44)<=0)
                    throw new FormatException("Invalid stock launch force factors.");
            }
        }
        internal bool UsesStockBody { get { return stockBody!=null; } }
        internal bool SupportsVehicle(string kart, Dictionary<string,object> upgrades)
        {
            object capture,upgrade;
            if(!upgrades.TryGetValue(kart,out upgrade))return false;
            if(data==null)return true;
            return ProfileJson.Object(data,"vehicles").TryGetValue(kart,out capture) &&
                Convert.ToInt32(ProfileJson.Object(capture)["engineType"])==Convert.ToInt32(ProfileJson.Object(upgrade)["engineType"]);
        }

        // FKtKartBodyParam's Steam serializer writes 55 scalars in memory
        // order. Skip only native alignment padding, preserving all fields.
        internal static byte[] PackClientBody(byte[] native)
        {
            if (native==null || native.Length!=0xd8 || !BitConverter.IsLittleEndian)
                throw new FormatException("Unexpected Steam vehicle body layout.");
            var packed=new byte[205];
            int cursor=0;
            for(int offset=0;offset<native.Length;) {
                if(offset==0x18 || offset==0x9c || offset==0xa8 || offset==0xbc) {
                    int count=offset==0xa8 ? 2 : 1;
                    for(int index=0;index<count;++index) {
                        byte flag=native[offset+index];
                        if(flag>1)throw new FormatException("Invalid Steam vehicle flag.");
                        packed[cursor++]=flag;
                    }
                } else {
                    if(offset!=0x8c && offset!=0x94 && offset!=0xac && offset!=0xb0 && offset!=0xb4) {
                        float field=BitConverter.ToSingle(native,offset);
                        if(Single.IsNaN(field) || Single.IsInfinity(field) || (offset==0 && field<=0))
                            throw new FormatException("Invalid Steam vehicle scalar.");
                    }
                    Buffer.BlockCopy(native,offset,packed,cursor,4);
                    cursor+=4;
                }
                offset+=4;
            }
            return packed;
        }

        public Dictionary<string, object> Payload(Dictionary<string, object> state, string account, string shared, string kart = null)
        {
            if (kart == null) kart = ProfileJson.Text(ProfileJson.Object(ProfileJson.Object(state, "itemPresetOn"), "itemMap"), "Kart");
            var upgrade = ProfileJson.Object(ProfileJson.Object(state, "ownKartsUpgradeInfo"), kart);
            var settings = ProfileJson.Object(ProfileJson.Object(upgrade,"gearInfo"),"gearSettings");
            byte[] body;
            if(data==null) {
                // The client's own neutral body is shared by this experimental
                // profile. Keep setup honest about unavailable tuning effects.
                foreach(var value in settings.Values)if(Convert.ToInt32(value)!=0)
                    throw new ArgumentException("No kart-specific tuning values are available for the stock body.");
                body=(byte[])stockBody.Clone();
            } else {
                body=ApplyCapturedBody(kart,upgrade,steamTuning==null?settings:Neutral);
                if(steamTuning!=null)steamTuning.Apply(body,settings);
            }
            return Encrypt(body,account,shared);
        }

        byte[] ApplyCapturedBody(string kart,Dictionary<string,object> upgrade,Dictionary<string,object> settings)
        {
            var captures = ProfileJson.Object(data, "vehicles");
            object entry;
            if (!captures.TryGetValue(kart, out entry)) throw new ArgumentException("Vehicle has no external physics capture.");
            var capture = ProfileJson.Object(entry);
            if (Convert.ToInt32(upgrade["engineType"]) != Convert.ToInt32(capture["engineType"]))
                throw new ArgumentException("Engine representation differs from external capture.");
            byte[] body = capture.ContainsKey("nativeBase")
                ? PackClientBody(Convert.FromBase64String(ProfileJson.Text(capture,"nativeBase")))
                : Convert.FromBase64String(ProfileJson.Text(capture, "packedBase"));
            var fields = ProfileJson.Object(data, "fields");
            var before = Effect(ProfileJson.Object(capture, "settings"));
            var after = Effect(settings);
            var changed = new HashSet<string>(before.Keys, StringComparer.Ordinal);
            changed.UnionWith(after.Keys);
            foreach (string name in changed)
            {
                var field = ProfileJson.Object(fields, name);
                int offset = Convert.ToInt32(field["offset"]);
                string kind = ProfileJson.Text(field, "kind");
                double oldValue = before.ContainsKey(name) ? before[name] : 0;
                double newValue = after.ContainsKey(name) ? after[name] : 0;
                if (oldValue == newValue) continue;
                if (offset < 0 || offset + (kind == "bool" ? 1 : 4) > body.Length) throw new FormatException("Field outside body.");
                if (kind == "bool") body[offset] = (byte)(newValue != 0 ? 1 : 0);
                else if (kind == "u32")
                {
                    uint current = BitConverter.ToUInt32(body, offset);
                    long value = checked((long)current - (long)oldValue + (long)newValue);
                    if (value < 0 || value > UInt32.MaxValue) throw new ArgumentOutOfRangeException(name);
                    Buffer.BlockCopy(BitConverter.GetBytes((uint)value), 0, body, offset, 4);
                }
                else if (kind == "f32")
                {
                    float value = (float)((float)(BitConverter.ToSingle(body, offset) - (float)oldValue) + (float)newValue);
                    if (Single.IsNaN(value) || Single.IsInfinity(value)) throw new FormatException("Nonfinite physics value.");
                    Buffer.BlockCopy(BitConverter.GetBytes(value), 0, body, offset, 4);
                }
                else throw new FormatException("Unknown scalar encoding.");
            }
            return body;
        }

        static Dictionary<string,object> Encrypt(byte[] body,string account,string shared)
        {
            byte[] key, iv;
            using (var sha = SHA256.Create())
            {
                key = sha.ComputeHash(Encoding.UTF8.GetBytes(account + shared + "_key"));
                var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(account + shared + "_iv"));
                iv = new byte[16]; Buffer.BlockCopy(digest, 0, iv, 0, 16);
            }
            byte[] encrypted;
            using (var aes = Aes.Create())
            {
                aes.Key = key; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                using (var transform = aes.CreateEncryptor()) encrypted = transform.TransformFinalBlock(body, 0, body.Length);
            }
            Array.Clear(key, 0, key.Length);
            return new Dictionary<string, object> { { "speedBodyParamStr", Convert.ToBase64String(encrypted) }, { "itemBodyParamStr", "" } };
        }
        Dictionary<string, double> Effect(Dictionary<string, object> settings)
        {
            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var rule in ProfileJson.Object(data, "effects"))
            {
                object raw;
                int level = settings.TryGetValue(rule.Key, out raw) ? Convert.ToInt32(raw) : 0;
                if (level <= 0) continue;
                var levels = ProfileJson.Object(rule.Value);
                object row;
                if (!levels.TryGetValue(level.ToString(System.Globalization.CultureInfo.InvariantCulture), out row))
                    throw new ArgumentException("No external effect row for requested level.");
                foreach (var field in ProfileJson.Object(row))
                {
                    // Each output field has exactly one controlling rule. An
                    // overlap needs an independently verified operation order.
                    if (result.ContainsKey(field.Key)) throw new FormatException("Overlapping effect fields.");
                    result.Add(field.Key, Convert.ToDouble(field.Value));
                }
            }
            return result;
        }
    }
}
