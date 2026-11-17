using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TwinKartLauncher.Gateway
{
    internal static class OfflineBalloonDefinitions
    {
        internal const string SetDocument="BalloonSetDef", EffectDocument="BalloonEffectSetDef";
        static readonly Regex ResourceId=new Regex(@"\ARC:10001[0-9]{7}\z",RegexOptions.CultureInvariant);
        static readonly string[] Grades={"common","uncommon","rare","unique","legendary"};

        internal static bool IsBalloon(string id) {return ResourceId.IsMatch(id);}

        internal static object InventoryRules(IEnumerable<string> installedItems)
        {
            var ownership=installedItems.Where(IsBalloon).OrderBy(id=>id,StringComparer.Ordinal)
                .ToDictionary(id=>id,id=>"basic",StringComparer.Ordinal);
            return new {
                maxGrade=3,
                balloonDef=new {
                    basic=Progression("DefenseMissile","BoosterIncrease","FastEscape","DefenseThunderboltUfo"),
                    basic2=Progression("FastEscape","BoosterIncrease")
                },
                balloonItem=ownership
            };
        }

        static object Progression(params string[] effects)
        {
            return new {chargeCost=30,chargeMax=1000,grade=effects.Select((effect,level)=>new {
                effect=new[]{effect},cost=500*level
            }).ToArray()};
        }

        internal static object Effects()
        {
            return new {effect=new {
                BoosterIncrease=SpeedBoost(false),BoosterIncrease2=SpeedBoost(true),
                DefenseMissile=Effect(1,"Item",level=>Stage("",new {bananaSlip=1})),
                DefenseThunderboltUfo=Effect(4,"Item",level=>Stage("",new {
                    ThunderboltTimeReduction=level>=3?0.2f:0.0f,UfoTimeReduction=level>=3?0.3f:0.0f
                })),
                FastEscape=Effect(3,"Item",FastEscape)
            }};
        }

        static object Effect(int key,string raceType,Func<int,object> grade)
        {
            string text="Balloon_Effect"+key.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new {titleStringKey=text+"_Title",descStringKey=text+"_Desc",raceType,
                grade=Enumerable.Range(0,Grades.Length).Select(grade).ToArray()};
        }

        static object Stage(string figure,object parameters,int boost=0)
        {
            return new {figureStringKey=figure,param=parameters,bodyParam=new {
                forwardAccelForce=0,backwardAccelForce=0,chargeBoostBySpeed=boost
            }};
        }

        static object SpeedBoost(bool fixedBoost)
        {
            int[] speedCharge={0,35,60,85,120};
            return Effect(2,"Speed",level=>Stage(level==0?"":"Balloon_Effect2_Figure_"+Grades[level],
                new Dictionary<string,object>(),fixedBoost?speedCharge[4]:speedCharge[level]));
        }

        static object FastEscape(int level)
        {
            int[] mobileBomb={6,6,4,3,2}, pcBomb={10,10,7,6,5};
            int[] mobileFly={4,4,3,2,1}, pcFly={6,6,4,3,2};
            int figureType=level==4?2:3;
            string figure=level<2?"":"Balloon_Effect"+figureType.ToString(System.Globalization.CultureInfo.InvariantCulture)+"_Figure_"+Grades[level];
            return Stage(figure,new {bombinputCountConsole=mobileBomb[level],bombinputCountMobile=mobileBomb[level],
                bombinputCountPC=pcBomb[level],flyinputCountConsole=mobileFly[level],flyinputCountMobile=mobileFly[level],
                flyinputCountPC=pcFly[level]});
        }
    }
}
