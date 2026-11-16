using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using TwinKartLauncher;

static class PortableSetup
{
    const string GameHash="5041d9f5d45adb4b1c932d8056a89f70db7fa98f93614ac30685cd1adeb9d9c2";
    const string OriginalSdkHash="456233bf2e7ef57f40663d4f808bb44c8dee3eab7b5437cf55b605577b5dfc04";
    const string PreparedSdkHash="92375d65a397d6e399cdbcc7483c0523b6ec1648e5439d9b264afd9415e6dd36";
    const string GameExe="KartDrift-Win64-Shipping.exe";
    const string Win64=@"KartDrift\Binaries\Win64";
    const string Sdk="nexon_api_x64.dll";
    const string ManifestFile="manifest.private.local.json";
    const int Port=59874;
    static readonly string Tools=AppDomain.CurrentDomain.BaseDirectory;
    static readonly string[] DataFiles={"initial-profile.private.json","client-defaults.private.json","unlock-catalog.private.json",
        "gear-rules.private.json","tracks.private.json","native-parameters.private.json","vehicle-parts.private.json","dynamic.private.json","track-report.private.json"};
    static readonly string[] AuthFields={"TransportKey","HashSecret","PreAuthKey","ExpectedClientVersion","AccountPlatform","LocalAccountId","ProtocolHash"};
    static readonly JavaScriptSerializer Json=new JavaScriptSerializer {MaxJsonLength=32*1024*1024,RecursionLimit=96};

    static void Require(bool ok,string message) { if(!ok)throw new InvalidDataException(message); }
    static string Hash(string path) {return ProfileStore.Fingerprint(path);}
    static string Quote(string path) {return "\""+path+"\"";}
    static string DefaultState()
    {return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"TwinKartLauncher","Drift");}
    static Dictionary<string,object> ReadMap(string path)
    {
        Require(File.Exists(path) && new FileInfo(path).Length<=32768,"Missing or invalid private configuration.");
        return Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path));
    }
    static void Write(string path,object value) {File.WriteAllText(path,Json.Serialize(value),new UTF8Encoding(false));}
    static LaunchModule Pin(string path) {return new LaunchModule {Path=path,Sha256=Hash(path)};}
    static void RunTool(string name,string args)
    {
        using(var process=Process.Start(new ProcessStartInfo(Path.Combine(Tools,name),args){UseShellExecute=false,WorkingDirectory=Tools})) {
            if(!process.WaitForExit(45000)) {
                process.Kill();
                process.WaitForExit();
                throw new TimeoutException(name+" did not finish.");
            }
            Require(process.ExitCode==0,name+" could not validate or prepare the local installation.");
        }
    }
    static void CheckIdle(string state)
    {
        foreach(var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GameExe)))
            using(process) {Require(process.HasExited,"Exit Drift before preparing an installation.");}
        Require(!File.Exists(Path.Combine(state,"sessions","pending-session.json")),"The previous session needs configuration recovery.");
    }
    static string SteamRoot(string userPath)
    {
        if(!String.IsNullOrWhiteSpace(userPath))return Path.GetFullPath(userPath);
        string found=ClientDiscovery.FindDrift();
        Require(File.Exists(found),"Drift was not found in Steam. Supply --steam-root pointing to the folder containing KartDrift.");
        return new FileInfo(found).Directory.Parent.Parent.Parent.FullName;
    }
    static string ValidateClient(string root,string expectedSdk)
    {
        string bin=Path.Combine(root,Win64);
        string game=Path.Combine(bin,GameExe);
        Require(File.Exists(game) && Hash(game)==GameHash,"Unsupported or incomplete Steam client. This setup requires Drift build 15490952.");
        string sdk=Path.Combine(bin,Sdk);
        Require(File.Exists(sdk) && Hash(sdk)==expectedSdk,"The Nexon runtime does not match this client build.");
        string paks=Path.Combine(root,@"KartDrift\Content\Paks");
        Require(Directory.Exists(paks) && Directory.Exists(Path.Combine(root,"Engine")) && Directory.Exists(Path.Combine(root,@"KartDrift\Plugins")),
            "Install the complete Windows Steam client before running setup.");
        var files=new DirectoryInfo(paks).GetFiles("*.pak",SearchOption.AllDirectories);
        Require(files.Length==28 && files.Sum(file=>file.Length)==27934556284L,
            "Steam package count or size differs. Finish downloading or verify the files in Steam.");
        return game;
    }
    static Dictionary<string,object> Auth(string path)
    {
        var input=ReadMap(path);
        var output=new Dictionary<string,object>(StringComparer.Ordinal);
        foreach(string name in AuthFields) {
            object value;
            Require(input.TryGetValue(name,out value) && value!=null,"Private setup data is missing "+name+".");
            output.Add(name,value);
        }
        var preAuth=output["PreAuthKey"] as string;
        Require(!String.IsNullOrWhiteSpace(preAuth) && preAuth.IndexOfAny(new[]{'\0','\r','\n'})<0,"Invalid private pre-auth configuration.");
        return output;
    }
    static void CheckBundle(string bundle)
    {
        Auth(Path.Combine(bundle,"gateway-auth.private.json"));
        foreach(string file in DataFiles) {
            var info=new FileInfo(Path.Combine(bundle,file));
            Require(info.Exists && info.Length>0 && info.Length<=32*1024*1024,"Private setup data is missing or invalid: "+file+".");
        }
    }
    static void Export(string manifestPath,string destination)
    {
        var manifest=Json.Deserialize<OfflineLaunchManifest>(File.ReadAllText(Path.GetFullPath(manifestPath)));
        Require(manifest!=null && manifest.GameSha256==GameHash,"Select a working build 15490952 launch manifest.");
        var gateway=ReadMap(manifest.GatewayConfigPath);
        var auth=Auth(manifest.GatewayConfigPath);
        string data=(string)gateway["ProfileDataDirectory"];
        string dynamic=(string)gateway["DynamicDataPath"];
        string trackReport=manifest.TrackReportPath;
        Require(!Directory.Exists(destination) && !File.Exists(destination),"Choose a new private export folder.");
        var source=DataFiles.ToDictionary(file=>file,file=>Path.Combine(data,file));
        source["dynamic.private.json"]=dynamic;
        source["track-report.private.json"]=trackReport;
        foreach(var row in source) Require(File.Exists(row.Value),"Source setup data is missing: "+row.Key+".");
        Directory.CreateDirectory(destination);
        foreach(var row in source)File.Copy(row.Value,Path.Combine(destination,row.Key));
        object appearance;
        if(gateway.TryGetValue("VehicleAvailabilityPath",out appearance) && File.Exists(appearance as string))
            File.Copy((string)appearance,Path.Combine(destination,"appearance.private.json"));
        Write(Path.Combine(destination,"gateway-auth.private.json"),auth);
        CheckBundle(destination);
        Console.WriteLine("Private setup data exported. Transfer only to machines you control.");
    }
    static void CopyTree(string from,string to,string exclude)
    {
        Require((File.GetAttributes(from)&FileAttributes.ReparsePoint)==0,"Steam resource folders must not contain redirects.");
        Directory.CreateDirectory(to);
        foreach(string directory in Directory.EnumerateDirectories(from))
            CopyTree(directory,Path.Combine(to,Path.GetFileName(directory)),exclude);
        foreach(string file in Directory.EnumerateFiles(from)) {
            if(String.Equals(file,exclude,StringComparison.OrdinalIgnoreCase))continue;
            Require((File.GetAttributes(file)&FileAttributes.ReparsePoint)==0,"Steam resource files must not contain redirects.");
            File.Copy(file,Path.Combine(to,Path.GetFileName(file)));
        }
    }
    static void PrepareGame(string steam,string target)
    {
        if(Directory.Exists(target)) {ValidateClient(target,PreparedSdkHash);return;}
        string temporary=target+".preparing-"+Guid.NewGuid().ToString("N").Substring(0,8);
        string installedSdk=Path.Combine(steam,Win64,Sdk);
        Console.WriteLine("Preparing an isolated copy of the Steam client. This copies about 28 GB of game data.");
        foreach(string folder in new[]{"Engine",@"KartDrift\Content",@"KartDrift\Plugins",Win64})
            CopyTree(Path.Combine(steam,folder),Path.Combine(temporary,folder),installedSdk);
        RunTool("TwinKartSdkCopy.exe",Quote(installedSdk)+" "+Quote(Path.Combine(temporary,Win64,Sdk)));
        ValidateClient(temporary,PreparedSdkHash);
        Directory.Move(temporary,target);
    }
    static void Setup(string state,string bundle,string userSteamRoot)
    {
        CheckIdle(state);
        CheckBundle(bundle);
        string steam=SteamRoot(userSteamRoot);
        ValidateClient(steam,OriginalSdkHash);
        Require(!state.StartsWith(steam.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase) &&
            !String.Equals(state,steam,StringComparison.OrdinalIgnoreCase),"Choose setup storage outside the original Steam installation.");
        var modules=new[]{"TwinKartDriftCamera.dll","TwinKartDriftRaceBridge.dll"}.Select(name=>Pin(Path.Combine(Tools,name))).ToArray();
        string gatewayExe=Path.Combine(Tools,"TwinKartGateway.exe");
        string gatewayHash=Hash(gatewayExe);
        Require(File.Exists(Path.Combine(Tools,"TwinKartSession.exe")) && File.Exists(Path.Combine(Tools,"TwinKartSdkCopy.exe")),
            "Use an extracted Windows release or build the launcher tools first.");
        string settings=Path.Combine(state,"config-"+Guid.NewGuid().ToString("N").Substring(0,8));
        Directory.CreateDirectory(settings);
        foreach(string file in DataFiles)File.Copy(Path.Combine(bundle,file),Path.Combine(settings,file));
        string appearance=Path.Combine(bundle,"appearance.private.json");
        if(File.Exists(appearance))File.Copy(appearance,Path.Combine(settings,"appearance.private.json"));
        var auth=Auth(Path.Combine(bundle,"gateway-auth.private.json"));
        var gateway=new Dictionary<string,object>(auth) {
            {"Port",Port},{"RunSeconds",3600},{"ProfileDataDirectory",settings},
            {"DynamicDataPath",Path.Combine(settings,"dynamic.private.json")},{"ProfilePath",Path.Combine(state,"profile.private.local.json")}
        };
        if(File.Exists(appearance))gateway["VehicleAvailabilityPath"]=Path.Combine(settings,"appearance.private.json");
        string gatewayPath=Path.Combine(settings,"gateway.private.local.json");
        Write(gatewayPath,gateway);
        RunTool("TwinKartGateway.exe","--check-offline "+Quote(gatewayPath));
        string trackReport=Path.Combine(settings,"track-report.private.json");
        NativeTrackSessionCatalog.PrepareQueries(trackReport,GameHash,Path.Combine(settings,"setup-track-queries.txt"));
        string target=Path.Combine(state,"Game");
        PrepareGame(steam,target);
        string config=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),@"KartDrift\Saved\Config\WindowsNoEditor");
        Directory.CreateDirectory(config);
        var env=new Dictionary<string,string>();
        foreach(string flag in new[]{"EVENT_MODES","FACTORY_COMPAT","CHASER_COMPAT","CHASER_ARENA","HIDE_COMPAT","HIDE_REPAIRED_SCENE"})
            env.Add("TWIN_KART_"+flag,"1");
        var manifest=new OfflineLaunchManifest {
            GamePath=Path.Combine(target,Win64,GameExe),GameSha256=GameHash,
            GameArguments="-nexonarena -serviceid:1050765443 -nexonsn:1 -sid:00000000-0000-0000-0000-000000000001 -Server:kd-live -httpproxy= -NOSPLASH -nohmd",
            GatewayConfigPath=gatewayPath,GatewaySha256=gatewayHash,ConfigDirectory=config,SessionDirectory=Path.Combine(state,"sessions"),
            Port=Port,MaximumSeconds=0,ServerSection=new Dictionary<string,string>{{"ServerAddrs","(Name=\"kd-live\",HttpUrl=\"ws://127.0.0.1:"+Port+"\")"},{"PreAuthKey",(string)auth["PreAuthKey"]}},
            Environment=env,Modules=modules,ClientFiles=new[]{Pin(Path.Combine(target,Win64,Sdk))},
            ExternalSdkProvenance="Prepared locally from the installed Steam SDK",EnableExperimentalRaceHost=true,TrackReportPath=trackReport,
            Keyboard=new OfflineKeyboardOptions {AddWasd=true},Language=new OfflineLanguageOptions()
        };
        string filePath=Path.Combine(state,ManifestFile),temporary=filePath+"."+Guid.NewGuid().ToString("N")+".tmp";
        Write(temporary,manifest);
        if(File.Exists(filePath))File.Replace(temporary,filePath,null);else File.Move(temporary,filePath);
        Console.WriteLine("Setup complete. Run TwinKartSetup.exe launch to play.");
    }
    static int Main(string[] args)
    {
        try {
            if(args.Length==3 && args[0]=="export-private") {Export(args[1],Path.GetFullPath(args[2]));return 0;}
            if(args.Length>=1 && (args[0]=="setup" || args[0]=="launch")) {
                var options=new Dictionary<string,string>();
                for(int i=1;i<args.Length;i+=2) {
                    Require(i+1<args.Length && new[]{"--private-data","--steam-root","--state"}.Contains(args[i]) &&
                        (args[0]=="setup" || args[i]=="--state"),"Invalid setup or launch option.");
                    options.Add(args[i],args[i+1]);
                }
                string state,steam,bundle;
                if(!options.TryGetValue("--state",out state))state=DefaultState();
                state=Path.GetFullPath(state);
                Directory.CreateDirectory(state);
                if(args[0]=="setup") {
                    Require(options.TryGetValue("--private-data",out bundle),"Supply --private-data with your private setup folder.");
                    options.TryGetValue("--steam-root",out steam);
                    using(var setupLock=new FileStream(Path.Combine(state,"setup.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
                        Setup(state,Path.GetFullPath(bundle),steam);
                } else {
                    string manifest=Path.Combine(state,ManifestFile);
                    Require(File.Exists(manifest),"Run setup for this machine first.");
                    using(var setupLock=new FileStream(Path.Combine(state,"setup.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
                    using(var process=Process.Start(new ProcessStartInfo(Path.Combine(Tools,"TwinKartSession.exe"),Quote(manifest)){UseShellExecute=false,WorkingDirectory=Tools})) {
                        process.WaitForExit();
                        return process.ExitCode;
                    }
                }
                return 0;
            }
            Console.WriteLine("TwinKartSetup.exe export-private <working manifest> <new private folder>");
            Console.WriteLine("TwinKartSetup.exe setup --private-data <folder> [--steam-root <folder>] [--state <folder>]");
            Console.WriteLine("TwinKartSetup.exe launch [--state <folder>]");
            return args.Length==0?0:2;
        } catch(Exception error) {
            Console.Error.WriteLine("Twin Kart setup: "+error.Message);
            return 1;
        }
    }
}
