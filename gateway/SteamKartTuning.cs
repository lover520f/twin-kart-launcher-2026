using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TwinKartLauncher.Gateway
{
    public sealed class SteamKartTuning
    {
        // The verified Steam body modifier selects by points and visits rules
        // in table order. These offsets refer to the 205-byte wire body.
        static readonly int[] PackedFields={0x19,0x08,0x21,0x25,0x71,0x6d,0x75,0x3d,0x41,0x31,0x35,0x85};
        const int LongSlide=0xa3;
        static readonly string[] RuleNames={"TransAccelFactor","DriftEscapeForce","BoosterTime","DriftMaxGauge",
            "ForceSlipEscapeFactor","DriftGaugePreservePercent","BoosterAccel","DraftMulAccelFactor","AutoLongSlideDrift"};
        readonly Dictionary<string,Dictionary<int,Variant>> rules=new Dictionary<string,Dictionary<int,Variant>>(StringComparer.Ordinal);
        readonly List<string> order=new List<string>();

        sealed class Variant
        {
            public readonly float[] Values=new float[PackedFields.Length];
            public bool LongSlide;
        }

        internal void Add(string[] columns)
        {
            int points;
            if(columns.Length!=4+PackedFields.Length || Array.IndexOf(RuleNames,columns[1])<0 ||
                !Int32.TryParse(columns[2],NumberStyles.None,CultureInfo.InvariantCulture,out points) || points<1 || points>10)
                throw new FormatException("Unexpected owned Steam gear rule.");
            var variant=new Variant();
            for(int index=0;index<variant.Values.Length;++index) {
                uint bits;
                string cell=columns[index+3];
                if(cell.Length!=8 || !UInt32.TryParse(cell,NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out bits))
                    throw new FormatException("Invalid Steam gear scalar.");
                float value=BitConverter.ToSingle(BitConverter.GetBytes(bits),0);
                if(Single.IsNaN(value) || Single.IsInfinity(value))throw new FormatException("Nonfinite Steam gear scalar.");
                variant.Values[index]=value;
            }
            string flag=columns[columns.Length-1];
            if(flag!="0" && flag!="1")throw new FormatException("Invalid Steam gear flag.");
            variant.LongSlide=flag=="1";
            Dictionary<int,Variant> levels;
            if(!rules.TryGetValue(columns[1],out levels)) {
                levels=new Dictionary<int,Variant>();rules.Add(columns[1],levels);order.Add(columns[1]);
            } else if(order[order.Count-1]!=columns[1])throw new FormatException("Interleaved Steam gear rules.");
            if(levels.ContainsKey(points))throw new FormatException("Duplicate Steam gear point level.");
            levels.Add(points,variant);
        }

        internal string GarageRules()
        {
            if(rules.Count!=RuleNames.Length || !BitConverter.IsLittleEndian)
                throw new FormatException("Incomplete owned Steam gear table.");
            var output=new Dictionary<string,object>(StringComparer.Ordinal);
            foreach(string name in order) {
                var levels=rules[name];
                bool toggle=name=="AutoLongSlideDrift";
                if(toggle ? levels.Count!=1 || !levels.ContainsKey(5) || !levels[5].LongSlide :
                    levels.Count!=10 || levels.Values.Any(level=>level.LongSlide))
                    throw new FormatException("Incomplete Steam gear point levels.");
                output.Add(name,new {values=new[]{0}.Concat(levels.Keys.OrderBy(points=>points)).ToArray()});
            }
            return ProfileJson.Write(new {rules=output});
        }

        internal void Apply(byte[] body,Dictionary<string,object> settings)
        {
            if(body.Length!=205 || body[LongSlide]>1)throw new FormatException("Invalid Steam gear target body.");
            foreach(string name in order) {
                object setting;
                if(!settings.TryGetValue(name,out setting))continue;
                int points=Convert.ToInt32(setting,CultureInfo.InvariantCulture);
                if(points==0)continue;
                Variant variant;
                if(!rules[name].TryGetValue(points,out variant))throw new ArgumentException("No Steam gear rule for requested points.");
                for(int index=0;index<PackedFields.Length;++index) {
                    int offset=PackedFields[index];
                    float value=BitConverter.ToSingle(body,offset)+variant.Values[index];
                    if(Single.IsNaN(value) || Single.IsInfinity(value))throw new FormatException("Nonfinite tuned body field.");
                    Buffer.BlockCopy(BitConverter.GetBytes(value),0,body,offset,4);
                }
                if(variant.LongSlide)body[LongSlide]=1;
            }
        }
    }
}
