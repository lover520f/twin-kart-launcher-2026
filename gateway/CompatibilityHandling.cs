using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TwinKartLauncher.Gateway
{
    // A readable local handling configuration. This preserves measured offline
    // behavior while exposing missing factory provenance for every kart.
    internal sealed class CompatibilityHandling
    {
        const string GameHash="5041d9f5d45adb4b1c932d8056a89f70db7fa98f93614ac30685cd1adeb9d9c2";
        internal string PhysicsJson,RulesJson,DefaultPlanJson,CoverageJson;

        static void Need(bool valid)
        {if(!valid)throw new FormatException("Invalid local compatibility handling configuration.");}

        internal static CompatibilityHandling Parse(string text)
        {
            var data=ProfileJson.Parse(text);
            Need(Convert.ToInt32(data["schema"])==1 && Object.Equals(data["source"],"measured-local-compatibility") &&
                Object.Equals(data["factoryParametersVerified"],false) && Object.Equals(data["supportedGameSha256"],GameHash));
            var fields=ProfileJson.Object(data,"fields");
            var profiles=ProfileJson.Object(data,"bodyProfiles");
            var sourceVehicles=ProfileJson.Object(data,"vehicles");
            Need(fields.Count==55 && profiles.Count>0 && profiles.Count<=10000 && sourceVehicles.Count>0 && sourceVehicles.Count<=10000);
            var occupied=new bool[205];
            foreach(var pair in fields) {
                Need(Regex.IsMatch(pair.Key,@"\A[A-Za-z_][A-Za-z0-9_]{0,74}\z",RegexOptions.CultureInvariant));
                var field=ProfileJson.Object(pair.Value);
                string kind=ProfileJson.Text(field,"kind");
                int offset=Convert.ToInt32(field["offset"]),width=kind=="bool"?1:4;
                Need((kind=="bool" || kind=="f32" || kind=="u32") && offset>=0 && offset+width<=occupied.Length);
                for(int index=offset;index<offset+width;++index) {Need(!occupied[index]);occupied[index]=true;}
            }
            Need(occupied.All(value=>value));
            var packedProfiles=new Dictionary<string,string>(StringComparer.Ordinal);
            var bodyHashes=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var pair in profiles) {
                Need(Regex.IsMatch(pair.Key,@"\A[A-Za-z][A-Za-z0-9_-]{0,63}\z",RegexOptions.CultureInvariant));
                var values=ProfileJson.Object(pair.Value);
                Need(values.Count==fields.Count);
                var body=new byte[205];
                foreach(var entry in fields) {
                    var field=ProfileJson.Object(entry.Value);
                    int offset=Convert.ToInt32(field["offset"]);
                    object value;Need(values.TryGetValue(entry.Key,out value));
                    string kind=ProfileJson.Text(field,"kind");
                    if(kind=="bool") {
                        Need(value is bool);body[offset]=(byte)((bool)value?1:0);
                    } else {
                        byte[] encoded;
                        if(kind=="u32") {
                            decimal number=Convert.ToDecimal(value,CultureInfo.InvariantCulture);
                            Need(number>=0 && number<=UInt32.MaxValue && Decimal.Truncate(number)==number);
                            encoded=BitConverter.GetBytes((uint)number);
                        } else {
                            float number=Convert.ToSingle(value,CultureInfo.InvariantCulture);
                            Need(!Single.IsNaN(number) && !Single.IsInfinity(number));
                            encoded=BitConverter.GetBytes(number);
                        }
                        Buffer.BlockCopy(encoded,0,body,offset,encoded.Length);
                    }
                }
                // The original client combines channel defaults with body
                // adjustments. A zero body mass is a valid neutral input.
                Need(BitConverter.IsLittleEndian);
                packedProfiles.Add(pair.Key,Convert.ToBase64String(body));
                using(var sha=SHA256.Create())bodyHashes.Add(pair.Key,BitConverter.ToString(sha.ComputeHash(body)).Replace("-","").ToLowerInvariant());
            }
            var vehicles=new Dictionary<string,object>(StringComparer.Ordinal);
            var coverage=new List<object>();
            foreach(var pair in sourceVehicles) {
                Need(Regex.IsMatch(pair.Key,@"\AKart:[0-9]{1,18}\z",RegexOptions.CultureInvariant));
                var vehicle=ProfileJson.Object(pair.Value);
                int engine=Convert.ToInt32(vehicle["engineType"]);
                string profile=ProfileJson.Text(vehicle,"bodyProfile");
                Need((engine==0 || engine==1) && packedProfiles.ContainsKey(profile));
                vehicles.Add(pair.Key,new {engineType=engine,packedBase=packedProfiles[profile],settings=new Dictionary<string,object>()});
                coverage.Add(new {kart=pair.Key,engineType=engine,bodyProfile=profile,bodySha256=bodyHashes[profile],
                    source="measured-local-compatibility",factoryParametersVerified=false});
            }
            string rules=ProfileJson.Write(new {rules=ProfileJson.Object(data,"tuningRules")});
            var defaults=ProfileJson.Object(data,"defaultGearSettings");
            Need(defaults.Count>0 && defaults.Count<=64);
            string physics=ProfileJson.Write(new {fields=fields,vehicles=vehicles,effects=ProfileJson.Object(data,"tuningEffects")});
            string plan=ProfileJson.Write(new {karts=new[]{"*"},settings=defaults});
            // Validate the complete generated route before any file is written.
            var initial=ProfileJson.Write(new {ownKartsUpgradeInfo=sourceVehicles.ToDictionary(pair=>pair.Key,pair=>(object)new {
                engineType=ProfileJson.Object(pair.Value)["engineType"],gearInfo=new {gearSettings=new Dictionary<string,object>()}
            })});
            var tuned=ProfileJson.Parse(OfflineGarage.Apply(initial,rules,plan).ProfileJson);
            var parameters=new NativeKartParameters(physics);
            foreach(string kart in vehicles.Keys)parameters.Payload(tuned,"1","compatibility-validation",kart);
            return new CompatibilityHandling {
                PhysicsJson=physics,RulesJson=rules,DefaultPlanJson=plan,
                CoverageJson=ProfileJson.Write(new {schema=1,source="measured-local-compatibility",factoryParametersVerified=false,
                    vehicleCount=vehicles.Count,distinctBodyProfiles=profiles.Count,vehicles=coverage})
            };
        }

        internal static void Prepare(string input,string destination)
        {
            var configuration=Parse(ProfileJson.Read(input));
            destination=Path.GetFullPath(destination);
            if(Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
                throw new IOException("Compatibility output directory must be empty.");
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination,"native-parameters.private.json"),configuration.PhysicsJson,new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(destination,"gear-rules.private.json"),configuration.RulesJson,new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(destination,"initial-tuning.local.json"),configuration.DefaultPlanJson,new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(destination,"vehicle-parameter-coverage.local.json"),configuration.CoverageJson,new UTF8Encoding(false));
            Console.WriteLine("Prepared local compatibility handling. Original per-kart verification status is recorded in vehicle-parameter-coverage.local.json.");
        }
    }
}
