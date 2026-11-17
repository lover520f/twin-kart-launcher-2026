using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TwinKartLauncher.Gateway
{
    // Ownership is independent of asset/handling readiness. This public
    // baseline retains earlier unlocks without fabricating missing resources.
    internal sealed class CompatibilityCatalog
    {
        internal const string GameHash="5041d9f5d45adb4b1c932d8056a89f70db7fa98f93614ac30685cd1adeb9d9c2";
        internal string[] Items,Bodies,Tracks;
        internal Dictionary<string,Dictionary<string,object>> Characters,Vehicles;
        internal Dictionary<string,object> Upgrades;
        internal Dictionary<string,object> Common;
        static void Need(bool value) {if(!value)throw new FormatException("Invalid compatibility unlock baseline.");}
        static string[] Ids(Dictionary<string,object> root,string key,Regex syntax)
        {
            var raw=root[key] as object[];Need(raw!=null && raw.Length>0 && raw.Length<=10000);
            var seen=new HashSet<string>(StringComparer.Ordinal);
            foreach(object value in raw)Need(value is string && syntax.IsMatch((string)value) && seen.Add((string)value));
            return raw.Cast<string>().ToArray();
        }
        static Dictionary<string,Dictionary<string,object>> Outfits(Dictionary<string,object> root,string key,
            string prefix,string[] slots,HashSet<string> known)
        {
            var result=new Dictionary<string,Dictionary<string,object>>(StringComparer.Ordinal);
            foreach(var pair in ProfileJson.Object(root,key)) {
                Need(known.Contains(pair.Key) && pair.Key.StartsWith(prefix,StringComparison.Ordinal));
                var row=ProfileJson.Object(pair.Value);
                foreach(var part in row)Need(slots.Contains(part.Key) && part.Value is string && known.Contains((string)part.Value));
                result.Add(pair.Key,row);
            }
            Need(result.Count>=8 && result.Count<=4096);return result;
        }
        internal static CompatibilityCatalog Parse(string text)
        {
            var root=ProfileJson.Parse(text);
            Need(Object.Equals(root["schema"],1) && Object.Equals(root["source"],"retained-local-unlock-catalog") && Object.Equals(root["supportedGameSha256"],GameHash));
            var item=new Regex(@"\A[A-Za-z][A-Za-z_]*:[0-9]{1,18}\z",RegexOptions.CultureInvariant);
            var result=new CompatibilityCatalog {Items=Ids(root,"items",item),Bodies=Ids(root,"kartBodies",item),
                Tracks=Ids(root,"tracks",new Regex(@"\A[A-Za-z][A-Za-z0-9_]{0,95}\z",RegexOptions.CultureInvariant))};
            var known=new HashSet<string>(result.Items,StringComparer.Ordinal);
            foreach(string id in result.Bodies)Need(known.Contains(id) && id.StartsWith("Kart:",StringComparison.Ordinal));
            result.Common=ProfileJson.Object(root,"common");
            foreach(var pair in result.Common)Need(new[]{"PartPlate","KartBooster","Balloon","SkidMark"}.Contains(pair.Key) && pair.Value is string && known.Contains((string)pair.Value));
            result.Characters=Outfits(root,"characters","Character:",new[]{"Costume","MotionFinish","MotionRetire","MotionVictory","MotionDefeat"},known);
            result.Vehicles=Outfits(root,"vehicles","Kart:",new[]{"PartFront","PartSide","PartBack","PartWheel","PartHandle","PartPlate","KartBooster","Balloon","SkidMark"},known);
            result.Upgrades=new Dictionary<string,object>(StringComparer.Ordinal);
            foreach(var pair in ProfileJson.Object(root,"upgradeDefinitions")) {
                var row=ProfileJson.Object(pair.Value);Need(known.Contains(pair.Key) && pair.Key.StartsWith("Kart:",StringComparison.Ordinal));
                int engine=Convert.ToInt32(row["engineType"]),grade=Convert.ToInt32(row["grade"]),level=Convert.ToInt32(row["level"]);
                Need((engine==0 || engine==1) && grade>=-1 && grade<=10 && level>=-1 && level<=100);
                result.Upgrades.Add(pair.Key,new {engineType=engine,grade=grade,level=level,gearInfo=new {
                    gearSettings=new Dictionary<string,object>(),totalGearCount=OfflineTuning.DisplayGearBalance,remainGearCount=OfflineTuning.DisplayGearBalance}});
            }
            return result;
        }
        internal static void Prepare(string input,string destination)
        {
            string text=ProfileJson.Read(input);var catalog=Parse(text);
            string jsonPath=Path.Combine(destination,"catalog-baseline.local.json"),itemsPath=Path.Combine(destination,"inventory-baseline.local.txt"),tracksPath=Path.Combine(destination,"tracks-baseline.local.json");
            if(!Directory.Exists(destination) || new[]{jsonPath,itemsPath,tracksPath}.Any(File.Exists))throw new IOException("Use a prepared directory without baseline files.");
            File.WriteAllText(jsonPath,text,new UTF8Encoding(false));
            File.WriteAllText(itemsPath,"TKITEMS1 "+GameHash+"\n"+String.Join("\n",catalog.Items)+"\n",new UTF8Encoding(false));
            File.WriteAllText(tracksPath,ProfileJson.Write(new {supportedGameSha256=GameHash,tracks=catalog.Tracks}),new UTF8Encoding(false));
            Console.WriteLine("Preserved compatibility catalog: "+catalog.Items.Length+" items, "+catalog.Bodies.Length+" vehicle IDs, "+catalog.Tracks.Length+" track IDs.");
        }
    }
}
