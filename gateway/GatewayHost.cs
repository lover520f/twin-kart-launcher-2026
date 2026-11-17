using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Web.Script.Serialization;

namespace TwinKartLauncher.Gateway
{
    static class GatewayHost
    {
        static int Main(string[] args)
        {
            if(args.Length==3 && args[0]=="--prepare-compatibility-catalog") {
                try {CompatibilityCatalog.Prepare(args[1],args[2]);return 0;}
                catch(Exception error) {Console.Error.WriteLine("Catalog setup failed: "+error.Message);return 1;}
            }
            if(args.Length==3 && args[0]=="--prepare-compatibility-config") {
                try {CompatibilityHandling.Prepare(args[1],args[2]);return 0;}
                catch(Exception error) {Console.Error.WriteLine("Compatibility setup failed: "+error.Message);return 1;}
            }
            bool check = args.Length == 2 && (args[0] == "--check-offline" || args[0] == "--check-offline-client-wire");
            bool runtimeWire = args.Length == 2 && (args[0] == "--offline-client-wire" || args[0] == "--check-offline-client-wire");
            bool offline = check || runtimeWire || (args.Length == 2 && args[0] == "--offline");
            bool localAccount = args.Length == 2 && (args[0] == "--local-account" || offline);
            if (args.Length != 1 && !localAccount)
            { Console.Error.WriteLine("Usage: TwinKartGateway.exe [--local-account|--offline|--offline-client-wire|--check-offline|--check-offline-client-wire] <private.local.json>"); return 2; }
            try
            {
                var file = new FileInfo(args[args.Length - 1]);
                if (!file.Exists || file.Length > 32768) throw new ArgumentException("Invalid config file");
                var settings = new JavaScriptSerializer().Deserialize<LocalAccountSettings>(File.ReadAllText(file.FullName));
                if (settings == null) throw new ArgumentException("Missing config");
                if(settings.ReadChannelDefaultsFromOwnedClient && (!runtimeWire || !settings.UseLocalDefaults))
                    throw new ArgumentException("Client-derived channel requires the owned launch stream and local catalog.");
                if(settings.ReadProtocolHashFromOwnedClient && !runtimeWire)
                    throw new ArgumentException("Client-derived digest requires the owned launch session.");
                Func<string> protocolDigest=null;
                if(settings.ReadProtocolHashFromOwnedClient && !check) {
                    string digestPath=Environment.GetEnvironmentVariable("TWIN_KART_PROTOCOL_DIGEST_PATH");
                    if(String.IsNullOrWhiteSpace(digestPath) || !Path.IsPathRooted(digestPath))
                        throw new ArgumentException("Owned protocol digest path unavailable.");
                    protocolDigest=()=>ReadProtocolDigest(digestPath);
                }
                if(settings.ReadVehicleDefaultsFromOwnedClient && (!settings.ReadChannelDefaultsFromOwnedClient || !settings.CreateStarterProfile))
                    throw new ArgumentException("Client vehicle defaults require the owned channel and local profile.");
                if(settings.ReadInventoryFromOwnedClient && (!runtimeWire || !settings.CreateStarterProfile || !settings.UseLocalDefaults ||
                    !String.IsNullOrEmpty(settings.VehicleAvailabilityPath)))
                    throw new ArgumentException("Client inventory requires the owned launch session and registration evidence.");
                if(settings.UseCompatibilityHandling && (!settings.ReadInventoryFromOwnedClient || settings.ReadVehicleDefaultsFromOwnedClient))
                    throw new ArgumentException("Compatibility handling requires a Steam inventory session and its configured parameter catalog.");
                string ownedInventory= null, inventoryFingerprint=null;
                if(settings.ReadInventoryFromOwnedClient && !check) {
                    ownedInventory=Environment.GetEnvironmentVariable("TWIN_KART_STEAM_INVENTORY_PATH");
                    inventoryFingerprint=Environment.GetEnvironmentVariable("TWIN_KART_ROOM_CLIENT_SHA256");
                    if(String.IsNullOrWhiteSpace(ownedInventory) || !Path.IsPathRooted(ownedInventory) || String.IsNullOrEmpty(inventoryFingerprint))
                        throw new ArgumentException("Owned inventory capture path unavailable.");
                }
                if (runtimeWire)
                {
                    if ((!check && !Console.IsInputRedirected) || !String.IsNullOrEmpty(settings.TransportKey) || !String.IsNullOrEmpty(settings.HashSecret))
                        throw new ArgumentException("Client wire setup requires the owned session's input stream.");
                    settings.ValidateSession();
                    settings.ValidateLocalIdentity();
                }
                else if (localAccount) settings.ValidateLocalAccount();
                else settings.Validate();
                bool offlineEvents=offline && Environment.GetEnvironmentVariable("TWIN_KART_EVENT_MODES")=="1";
                bool chaserArena=Environment.GetEnvironmentVariable("TWIN_KART_CHASER_COMPAT")=="1" && Environment.GetEnvironmentVariable("TWIN_KART_CHASER_ARENA")=="1";
                bool hideRepaired=Environment.GetEnvironmentVariable("TWIN_KART_HIDE_COMPAT")=="1" && Environment.GetEnvironmentVariable("TWIN_KART_HIDE_REPAIRED_SCENE")=="1";
                bool factoryRun=Environment.GetEnvironmentVariable("TWIN_KART_FACTORY_COMPAT")=="1";
                DynamicDataCatalog catalog=null;
                if(offline && settings.UseLocalDefaults) {
                    if(check || !settings.ReadChannelDefaultsFromOwnedClient)
                        catalog=DynamicDataCatalog.LoadLocal(settings.DynamicDataPath,offlineEvents,chaserArena,hideRepaired,factoryRun,
                            settings.ReadChannelDefaultsFromOwnedClient);
                }
                else if(localAccount && !String.IsNullOrEmpty(settings.DynamicDataPath))
                    catalog=DynamicDataCatalog.Load(settings.DynamicDataPath,offlineEvents,chaserArena,hideRepaired,factoryRun);
                if (check)
                {
                    if(settings.ReadVehicleDefaultsFromOwnedClient || settings.ReadInventoryFromOwnedClient) {
                        VerifyDeferredProfileInputs(settings);
                        Console.WriteLine("Offline input files validated. Remaining inputs come from the owned client at launch.");
                        return 0;
                    }
                    // Run the production profile loader against a fresh private
                    // scratch file. Do not bind sockets or change the current profile.
                    string scratch = Path.Combine(Path.GetTempPath(), "twinkart-check-"+Guid.NewGuid().ToString("N"));
                    try
                    {
                        OwnedSteamCatalog ignored;
                        var prepared = LoadProfile(out ignored, settings, scratch, 59875, null, null);
                        if (!runtimeWire) new LocalAccountSession(settings, catalog, prepared);
                    }
                    finally { if (File.Exists(scratch)) File.Delete(scratch); }
                    Console.WriteLine("Offline data validation passed.");
                    return 0;
                }
                using (var ping = offline ? new LocalPingServer() : null)
                {
                OfflineProfileStore profile = null;
                var roomLaunch=offline?NativeRoomLaunch.FromEnvironment():null;
                string raceControl=Environment.GetEnvironmentVariable("TWIN_KART_ROOM_RACE_CONTROL");
                bool deferredProfile=settings.ReadVehicleDefaultsFromOwnedClient || settings.ReadInventoryFromOwnedClient;
                if (offline && !deferredProfile) {
                    OwnedSteamCatalog ignored;
                    profile = LoadProfile(out ignored, settings, settings.ProfilePath, ping.Port,roomLaunch,raceControl);
                }
                using (var wireReady = new ManualResetEventSlim(!runtimeWire))
                {
                var wireAccepted = !runtimeWire;
                if (runtimeWire)
                {
                    var reader = new Thread(() => {
                        try {
                            settings.TransportKey = ReadRuntimeBase64(Console.In,32);
                            settings.HashSecret = ReadRuntimeBase64(Console.In,32);
                            byte[] vehicle=null,channel=null;
                            if(settings.ReadChannelDefaultsFromOwnedClient) {
                                channel=Convert.FromBase64String(ReadRuntimeBase64(Console.In,72));
                                if(settings.ReadVehicleDefaultsFromOwnedClient) {
                                    vehicle=Convert.FromBase64String(ReadRuntimeBase64(Console.In,216));
                                    // Native body mass starts at zero. The stock channel
                                    // establishes the real driving mass for this profile.
                                    Buffer.BlockCopy(channel,0,vehicle,0,4);
                                }
                            }
                            settings.ValidateLocalAccount();
                            OwnedSteamCatalog steamCatalog=null;
                            if(deferredProfile)
                                profile=LoadProfile(out steamCatalog,settings,settings.ProfilePath,ping.Port,roomLaunch,raceControl,vehicle,ownedInventory,inventoryFingerprint);
                            if(channel!=null || steamCatalog!=null)
                                catalog=DynamicDataCatalog.LoadLocal(settings.DynamicDataPath,offlineEvents,chaserArena,hideRepaired,factoryRun,
                                    settings.ReadChannelDefaultsFromOwnedClient,channel,
                                    steamCatalog==null?null:steamCatalog.BalloonItems);
                            wireAccepted = true;
                            Console.WriteLine("{\"event\":\"client_wire_ready\"}");
                        }
                        catch (IOException) { Console.Error.WriteLine("{\"event\":\"client_wire_rejected\"}"); }
                        catch (ArgumentException) { Console.Error.WriteLine("{\"event\":\"client_wire_rejected\"}"); }
                        catch (FormatException) { Console.Error.WriteLine("{\"event\":\"client_wire_rejected\"}"); }
                        catch (ObjectDisposedException) { }
                        catch (Exception error) { Console.Error.WriteLine("Client startup failed: " + error.GetType().Name); }
                        finally {
                            try { wireReady.Set(); }
                            catch (ObjectDisposedException) { }
                        }
                    }) {IsBackground=true};
                    reader.Start();
                }
                Func<Func<string, GatewayReply>> factory = () => {
                    Func<string, GatewayReply> handler = null;
                    return wire => {
                        if (handler == null)
                        {
                            if (!wireReady.Wait(settings.ReadInventoryFromOwnedClient?35000:15000) || !wireAccepted)
                                return GatewayReply.Close(1011, "client_wire_unavailable");
                            handler = localAccount ? (Func<string, GatewayReply>)new LocalAccountSession(settings,catalog,profile,protocolDigest).Handle
                                : new PreAuthSession(settings).Handle;
                        }
                        GatewayReply reply = handler(wire);
                        Console.WriteLine("{\"event\":\"" + reply.Event + "\",\"close_code\":" + reply.CloseCode + "}");
                        return reply;
                    };
                };
                using (var stop = new ManualResetEvent(false))
                using (var server = new LoopbackWebSocketServer(settings.Port, factory))
                {
                    ConsoleCancelEventHandler cancel = (sender, e) => { e.Cancel = true; stop.Set(); };
                    Console.CancelKeyPress += cancel;
                    try
                    {
                        Console.WriteLine("{\"event\":\"ready\",\"scope\":\"" + (offline ? "original-offline-profile" : localAccount ? "local-account-only" : "preauth-only") + "\",\"playable\":false,\"port\":" + server.Port + "}");
                        stop.WaitOne(settings.RunSeconds * 1000);
                    }
                    finally { Console.CancelKeyPress -= cancel; }
                }
                }
                }
                Console.WriteLine("{\"event\":\"stopped\"}"); return 0;
            }
            catch (Exception error)
            {
                // Values, file paths and exception messages may contain secrets.
                Console.Error.WriteLine("Gateway failed: " + error.GetType().Name); return 1;
            }
        }
        static string ReadProtocolDigest(string path)
        {
            var timer=Stopwatch.StartNew();
            while(timer.ElapsedMilliseconds<25000) {
                try {
                    using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                        if(file.Length!=64)return null;
                        var data=new byte[64];int cursor=0;
                        while(cursor<data.Length) {
                            int count=file.Read(data,cursor,data.Length-cursor);
                            if(count==0)return null;
                            cursor+=count;
                        }
                        return System.Text.Encoding.ASCII.GetString(data);
                    }
                } catch(FileNotFoundException) {Thread.Sleep(40);}
                  catch(IOException) {return null;}
                  catch(UnauthorizedAccessException) {return null;}
            }
            return null;
        }
        static string ReadRuntimeBase64(TextReader input,int size)
        {
            var buffer = new char[(size+2)/3*4];
            try {
                for (int i=0;i<buffer.Length;++i)
                {
                    int c=input.Read();
                    if (c<0 || !(c>='A' && c<='Z' || c>='a' && c<='z' || c>='0' && c<='9' || c=='+' || c=='/' || c=='='))
                        throw new ArgumentException("Invalid client wire constant.");
                    buffer[i]=(char)c;
                }
                int end=input.Read();
                if (end=='\r') end=input.Read();
                if (end!='\n') throw new ArgumentException("Invalid client wire constant length.");
                string value=new string(buffer);
                var bytes=Convert.FromBase64String(value);
                try {
                    if (bytes.Length!=size || !String.Equals(Convert.ToBase64String(bytes),value,StringComparison.Ordinal))
                        throw new ArgumentException("Invalid client wire encoding.");
                }
                finally {Array.Clear(bytes,0,bytes.Length);}
                return value;
            }
            finally {Array.Clear(buffer,0,buffer.Length);}
        }
        static void VerifyDeferredProfileInputs(LocalAccountSettings settings)
        {
            string data=settings.ProfileDataDirectory;
            if(!settings.ReadInventoryFromOwnedClient) {
                ProfileJson.Parse(ProfileJson.Read(Path.Combine(data,"gear-rules.private.json")));
                ProfileJson.Parse(ProfileJson.Read(Path.Combine(data,"unlock-catalog.private.json")));
                ProfileJson.Parse(ProfileJson.Read(Path.Combine(data,"vehicle-parts.private.json")));
            }
            if(settings.UseCompatibilityHandling)
                ProfileJson.Parse(ProfileJson.Read(Path.Combine(data,"gear-rules.private.json")));
            if(!settings.ReadVehicleDefaultsFromOwnedClient)
                ProfileJson.Object(ProfileJson.Parse(ProfileJson.Read(Path.Combine(data,"native-parameters.private.json"))),"vehicles");
        }
        static OfflineProfileStore LoadProfile(out OwnedSteamCatalog catalog, LocalAccountSettings settings, string profilePath, int pingPort,
            NativeRoomLaunch roomLaunch, string raceControl,byte[] vehicleDefaults=null,string ownedInventory=null,string inventoryFingerprint=null)
        {
            string data=settings.ProfileDataDirectory;
            if (!Directory.Exists(data) || String.IsNullOrEmpty(profilePath))
                throw new ArgumentException("Explicit external profile/data paths required");
            string physics=vehicleDefaults==null?ProfileJson.Read(Path.Combine(data,"native-parameters.private.json")):null;
            catalog=null;
            if(settings.ReadInventoryFromOwnedClient) {
                var timer=Stopwatch.StartNew();
                while(!File.Exists(ownedInventory) && timer.ElapsedMilliseconds<30000)Thread.Sleep(50);
                if(!File.Exists(ownedInventory))throw new IOException("The Steam inventory snapshot was not published.");
                string baseline=Path.Combine(data,"catalog-baseline.local.json");
                catalog=OwnedSteamCatalog.Load(ownedInventory,inventoryFingerprint,physics,
                    settings.UseCompatibilityHandling && File.Exists(baseline)?ProfileJson.Read(baseline):null);
                File.WriteAllText(Path.Combine(data,"catalog-coverage.local.json"),catalog.CoverageJson);
            }
            return new OfflineProfileStore(profilePath,
                settings.CreateStarterProfile ? null : ProfileJson.Read(Path.Combine(data, "initial-profile.private.json")),
                settings.UseLocalDefaults ? null : ProfileJson.Read(Path.Combine(data, "client-defaults.private.json")),
                catalog==null?ProfileJson.Read(Path.Combine(data,"unlock-catalog.private.json")):catalog.UnlockJson,
                catalog==null || settings.UseCompatibilityHandling?ProfileJson.Read(Path.Combine(data,"gear-rules.private.json")):catalog.TuningRules,
                settings.ReadTracksFromOwnedClient ? null : ProfileJson.Read(Path.Combine(data, "tracks.private.json")),
                physics,
                catalog==null?ProfileJson.Read(Path.Combine(data,"vehicle-parts.private.json")):catalog.PartsJson,
                settings.LocalAccountId, pingPort,
                String.IsNullOrEmpty(settings.TuningPlanPath) || (settings.UseCompatibilityHandling && File.Exists(profilePath)) ? null : ProfileJson.Read(settings.TuningPlanPath),
                catalog!=null?catalog.AvailabilityJson:String.IsNullOrEmpty(settings.VehicleAvailabilityPath) ? null : ProfileJson.Read(settings.VehicleAvailabilityPath),
                roomLaunch==null?(Func<RoomState,string>)null:roomLaunch.Start, raceControl,
                settings.ReadTracksFromOwnedClient ? (Func<TrackCatalog>)(() => roomLaunch==null ? new TrackCatalog(new string[0]) : roomLaunch.TimeAttackTracks()) : null,
                vehicleDefaults,catalog==null || settings.UseCompatibilityHandling?null:catalog.Tuning,catalog!=null && catalog.PreserveOwnership);
        }
    }
}
