using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TwinKartLauncher.Gateway
{
    internal sealed class OwnedSteamCatalog
    {
        static readonly Regex Identifier=new Regex(@"\A[A-Za-z][A-Za-z0-9_]{0,76}:[0-9]{1,18}\z",RegexOptions.CultureInvariant);
        static readonly string[] Character={"Character","Costume","MotionFinish","MotionRetire","MotionVictory","MotionDefeat"};
        static readonly string[] Kart={"Kart","PartFront","PartSide","PartBack","PartWheel","PartHandle","PartPlate","KartBooster","Balloon","SkidMark"};
        internal string UnlockJson {get;private set;}
        internal string PartsJson {get;private set;}
        internal string AvailabilityJson {get;private set;}
        internal SteamKartTuning Tuning {get;private set;}
        internal string TuningRules {get;private set;}
        internal string[] BalloonItems {get;private set;}

        static void Need(bool valid)
        { if(!valid)throw new FormatException("Invalid owned Steam inventory capture."); }

        internal static OwnedSteamCatalog Load(string path,string fingerprint,string physicsJson)
        {
            Need(fingerprint!=null && Regex.IsMatch(fingerprint,@"\A[0-9a-f]{64}\z",RegexOptions.CultureInvariant));
            var bodies=physicsJson==null?null:ProfileJson.Object(ProfileJson.Parse(physicsJson),"vehicles");
            var items=new SortedSet<string>(StringComparer.Ordinal);
            var characters=new SortedDictionary<string,Dictionary<string,object>>(StringComparer.Ordinal);
            var vehicles=new SortedDictionary<string,Dictionary<string,object>>(StringComparer.Ordinal);
            var unavailable=new SortedSet<string>(StringComparer.Ordinal);
            var kartComponents=new SortedSet<string>(StringComparer.Ordinal);
            var upgrades=new SortedDictionary<string,object>(StringComparer.Ordinal);
            var steamTuning=new SteamKartTuning();
            int registeredKartRows=0;bool outfits=false,gearing=false,closed=false;
            using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var source=new StreamReader(file,new UTF8Encoding(false,true),false)) {
                Need(file.Length>0 && file.Length<=4*1024*1024 && source.ReadLine()=="TKSTEAM2 "+fingerprint);
                string line;
                while((line=source.ReadLine())!=null) {
                    Need(!closed && line.Length>2 && line.Length<=1024);
                    string[] columns=line.Split(' ');
                    Need(columns.Length>=2);
                    if(columns[0]=="E") {
                        Need(columns.Length==5 && Matches(columns[1],items.Count) && Matches(columns[2],characters.Count) &&
                            Matches(columns[3],registeredKartRows) && Matches(columns[4],unavailable.Count));
                        closed=true;continue;
                    }
                    if(columns[0]=="G") {
                        Need(outfits);gearing=true;steamTuning.Add(columns);continue;
                    }
                    Need(!gearing);
                    string id=columns[1];
                    Need(Identifier.IsMatch(id));
                    if(columns[0]=="I") {
                        Need(!outfits && columns.Length==2 && items.Count<10000 && items.Add(id));
                    } else {
                        outfits=true;
                        Need(items.Contains(id) && !characters.ContainsKey(id) && !vehicles.ContainsKey(id) && !unavailable.Contains(id));
                        if(columns[0]=="X") {
                            Need(columns.Length==2 && id.StartsWith("Kart:",StringComparison.Ordinal));
                            unavailable.Add(id);continue;
                        }
                        bool rider=columns[0]=="C";
                        string[] keys=rider?Character:Kart;
                        Need((rider || columns[0]=="K") && columns.Length==keys.Length+1 && id.StartsWith(keys[0]+":",StringComparison.Ordinal));
                        var fields=new Dictionary<string,object>(StringComparer.Ordinal);
                        for(int slot=1;slot<keys.Length;++slot) {
                            string part=columns[slot+1];
                            if(part=="-")continue;
                            Need(items.Contains(part));
                            fields.Add(keys[slot],part);
                            // Kart is also the namespace for native modular
                            // wheels, steering wheels and body panels. A
                            // registered component needs no handling record.
                            if(!rider && part.StartsWith("Kart:",StringComparison.Ordinal) && part!=id)kartComponents.Add(part);
                        }
                        if(rider) {
                            object costume;
                            Need(fields.TryGetValue("Costume",out costume) && ((string)costume).StartsWith("Costume:",StringComparison.Ordinal));
                            characters.Add(id,fields);
                        } else {
                            vehicles.Add(id,fields);++registeredKartRows;
                        }
                    }
                }
            }
            Need(closed && characters.Count>=8 && vehicles.Count>=8 && characters.Count+vehicles.Count+unavailable.Count<=4096);

            // The asset registry proves the model exists. A captured body is
            // a separate prerequisite for kart-specific physics. Keep known
            // incompatible bodies visible to the inventory reconciliation.
            foreach(string id in vehicles.Keys.ToArray()) {
                int engine=0;object row;
                if(bodies!=null) {
                    if(!bodies.TryGetValue(id,out row)) {
                        unavailable.Add(id);vehicles.Remove(id);continue;
                    }
                    var body=ProfileJson.Object(row);
                    Need(body.TryGetValue("engineType",out row) && row is int && ((int)row==0 || (int)row==1));
                    engine=(int)row;
                }
                upgrades.Add(id,new {engineType=engine,grade=engine==1?3:0,level=0,gearInfo=new {
                    gearSettings=new Dictionary<string,object>(),totalGearCount=OfflineTuning.DisplayGearBalance,remainGearCount=OfflineTuning.DisplayGearBalance
                }});
            }
            Need(vehicles.Count>=8);
            // The item table can list karts without a parts row. Require
            // positive appearance and physics evidence before granting one.
            foreach(string id in items)
                if(id.StartsWith("Kart:",StringComparison.Ordinal) && !vehicles.ContainsKey(id) && !kartComponents.Contains(id)) unavailable.Add(id);
            var cartIds=new SortedSet<string>(vehicles.Keys,StringComparer.Ordinal);
            cartIds.UnionWith(unavailable);
            return new OwnedSteamCatalog {
                Tuning=steamTuning,TuningRules=steamTuning.GarageRules(),
                BalloonItems=items.Where(OfflineBalloonDefinitions.IsBalloon).ToArray(),
                UnlockJson=ProfileJson.Write(new {items=items.Select(id=>new {id=id,quantity=1}).ToArray(),kartBodies=cartIds.ToArray(),kartUpgrades=upgrades}),
                PartsJson=ProfileJson.Write(new {common=new Dictionary<string,object>(),characters=characters,vehicles=vehicles}),
                AvailabilityJson=ProfileJson.Write(new {
                    bodies=cartIds.Select(id=>new {id=id,registered=unavailable.Contains(id)?0:1}).ToArray(),
                    characters=characters.Select(row=>new {character=row.Key,costume=row.Value["Costume"],registered=1}).ToArray()
                })
            };
        }

        static bool Matches(string text,int count)
        { return text==count.ToString(CultureInfo.InvariantCulture); }
    }
}
