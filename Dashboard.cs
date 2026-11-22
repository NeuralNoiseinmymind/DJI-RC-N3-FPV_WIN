using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.IO.Ports;
using System.Collections.Generic;
using System.Threading;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using Microsoft.Win32;

namespace RcN3 {
    public class Axis {
        public int Input, Min = 364, Center = 1024, Max = 1684, Dead = 2, Expo;
        public bool Invert;
        public short Apply(int value) {
            double x = value < Center ? (double)(value-Center)/Math.Max(1,Center-Min)
                                      : (double)(value-Center)/Math.Max(1,Max-Center);
            x = Math.Max(-1,Math.Min(1,x));
            double a = Math.Abs(x), dead = Dead/100.0, expo = Expo/100.0;
            a = Math.Max(0,(a-dead)/(1-dead));
            a = (1-expo)*a + expo*a*a*a;
            x = Math.Sign(x)*a*(Invert ? -1 : 1);
            return (short)Math.Round(x < 0 ? 32768*x : 32767*x);
        }
    }

    public class Profile {
        public string Name = "FPV lineare";
        public override string ToString() { return Name; }
        public int Rate = 50;
        public OutputMode Output = OutputMode.Xbox360;
        public bool Buttons = true, Wheel;
        public ButtonBindings Bindings = new ButtonBindings();
        public Axis[] Axes = Enumerable.Range(0,4).Select(i => new Axis { Input=i }).ToArray();
    }

    public class SwitchBinding {
        public int Code;
        public string Profile;
    }

    public class Settings {
        public SwitchBinding[] ModeSwitch;

        public bool Enabled = true;
        public string Selected = "FPV lineare";
        public List<Profile> Profiles = new List<Profile>();
        public static Settings Defaults() { return new Settings { Profiles = new List<Profile> { new Profile() } }; }
    }

    static class Files {
        static readonly XmlSerializer Serializer = new XmlSerializer(typeof(Settings));
        static readonly byte[] Footer = Encoding.ASCII.GetBytes("RCN3-FPV-CONFIG1");
        public static readonly string Exe = Assembly.GetExecutingAssembly().Location;
        public static readonly string Config = Path.ChangeExtension(Exe,".profiles.xml");

        public static Settings Read() {
            if (File.Exists(Config)) using(var input = File.OpenRead(Config)) return ReadXml(input);
            using(var file=File.OpenRead(Exe)) {
                long start = BaseLength(file);
                if (start < file.Length) {
                    file.Position = start;
                    int length = (int)(file.Length-start-Footer.Length-4);
                    using(var reader=new BinaryReader(file))
                    using(var bytes=new MemoryStream(reader.ReadBytes(length))) return ReadXml(bytes);
                }
            }
            return Settings.Defaults();
        }

        public static Settings Import(string path) {
            using(var input=File.OpenRead(path)) return ReadXml(input);
        }

        public static void Export(string path, Settings config) {
            if (String.Equals(Path.GetFullPath(path),Config,StringComparison.OrdinalIgnoreCase))
                throw new IOException("Scegli una destinazione diversa dal file dei profili attivi.");
            WriteXml(path,config);
        }

        internal static Settings ReadXml(Stream stream) {
            using(var xml=XmlReader.Create(stream,new XmlReaderSettings {
                DtdProcessing=DtdProcessing.Prohibit, MaxCharactersInDocument=2000000
            })) {
                var result=(Settings)Serializer.Deserialize(xml);
                if (result.Profiles==null || result.Profiles.Count<1 || result.Profiles.Count>100 ||
                    result.Profiles.Any(p=>p==null) ||
                    result.Profiles.Select(p=>p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=result.Profiles.Count)
                    throw new InvalidDataException("Elenco profili non valido.");
                foreach(var p in result.Profiles) {
                    if (p == null || String.IsNullOrWhiteSpace(p.Name) || p.Name.Length>70 || p.Bindings==null || p.Axes==null || p.Axes.Length!=4 ||
                        p.Axes.Any(a=>a==null) || !p.Axes.Select(a=>a.Input).OrderBy(i=>i).SequenceEqual(new[]{0,1,2,3}) ||
                        !UsbRates.Supported.Contains(p.Rate) ||
                        p.Axes.Any(a=>a.Min<0 || a.Max>65535 || a.Center<=a.Min || a.Center>=a.Max ||
                            a.Dead<0 || a.Dead>30 || a.Expo<0 || a.Expo>100))
                        throw new InvalidDataException("Verifica i parametri di " + p.Name);
                }
                return result;
            }
        }

        public static void Save(Settings config) { WriteXml(Config,config); }

        static void WriteXml(string path, Settings config) {
            string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {
                using(var output=new FileStream(temp,FileMode.CreateNew)) Serializer.Serialize(output,config);
                if(File.Exists(path)) File.Replace(temp,path,null);
                else File.Move(temp,path);
            } finally { if(File.Exists(temp)) File.Delete(temp); }
        }

        static long BaseLength(Stream stream) {
            long length=stream.Length;
            if(length<Footer.Length+4) return length;
            stream.Seek(-Footer.Length-4,SeekOrigin.End);
            using(var reader=new BinaryReader(stream,Encoding.UTF8,true)) {
                int size=reader.ReadInt32();
                if(size>0 && size<=2000000 && size<length-Footer.Length-4 &&
                   reader.ReadBytes(Footer.Length).SequenceEqual(Footer)) return length-Footer.Length-4-size;
            }
            return length;
        }

        public static void CopyWithProfiles(string destination, Settings config) {
            if(String.Equals(Path.GetFullPath(destination),Exe,StringComparison.OrdinalIgnoreCase))
                throw new IOException("Scegli un nome diverso dall'eseguibile in uso.");
            string temp=destination+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {
                using(var xml=new MemoryStream())
                using(var source=File.OpenRead(Exe))
                using(var target=new BinaryWriter(new FileStream(temp,FileMode.CreateNew))) {
                    Serializer.Serialize(xml,config);
                    if(xml.Length>2000000) throw new IOException("I profili superano la dimensione supportata.");
                    long remaining=BaseLength(source);
                    source.Position=0;
                    byte[] buffer=new byte[65536];
                    while(remaining>0) {
                        int count=source.Read(buffer,0,(int)Math.Min(remaining,buffer.Length));
                        if(count==0) throw new EndOfStreamException();
                        target.Write(buffer,0,count);
                        remaining-=count;
                    }
                    target.Write(xml.ToArray());
                    target.Write((int)xml.Length);
                    target.Write(Footer);
                }
                if(File.Exists(destination)) File.Replace(temp,destination,null);
                else File.Move(temp,destination);
            } finally { if(File.Exists(temp)) File.Delete(temp); }
        }
    }

    static class Setup {
        const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "DJI RC-N3";
        static string Command { get { return "\"" + Files.Exe + "\" --background"; } }
        public static bool Startup {
            get {
                using(var key=Registry.CurrentUser.OpenSubKey(Run))
                    return key!=null && (string)key.GetValue(RunName,"")==Command;
            }
            set {
                using(var key=Registry.CurrentUser.CreateSubKey(Run)) {
                    if(value) key.SetValue(RunName,Command);
                    else if((string)key.GetValue(RunName,"")==Command) key.DeleteValue(RunName,false);
                }
            }
        }

        public static string Port() {
            string[] ports=SerialPort.GetPortNames();
            const string node=@"SYSTEM\CurrentControlSet\Enum\USB\VID_2CA3&PID_1020&MI_02";
            using(var key=Registry.LocalMachine.OpenSubKey(node)) {
                if(key==null) return null;
                foreach(string item in key.GetSubKeyNames()) {
                    using(var child=key.OpenSubKey(item+@"\Device Parameters")) {
                        string port=child==null?null:child.GetValue("PortName") as string;
                        if(ports.Contains(port,StringComparer.OrdinalIgnoreCase)) return port;
                    }
                }
            }
            return null;
        }

        public static string Extract(string resource, string path) {
            using(var input=Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            using(var file=new FileStream(path,FileMode.CreateNew)) {
                if(input==null) throw new IOException("Risorsa assente: "+resource);
                input.CopyTo(file);
            }
            return path;
        }

        public static bool HasDriverBundle {
            get {
                var names=Assembly.GetExecutingAssembly().GetManifestResourceNames();
                return names.Contains("ViGEmSetup") && names.Contains("SerialInf") && names.Contains("SerialCat");
            }
        }

        public static bool HasBus() {
            using(var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\ViGEmBus"))
                return key!=null;
        }

        static int RunElevated(string name, string arguments) {
            var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,name),arguments) {
                UseShellExecute=true, Verb="runas"
            };
            using(var process=Process.Start(start)) {
                process.WaitForExit();
                return process.ExitCode;
            }
        }

        public static string Install() {
            if(!Environment.Is64BitOperatingSystem) throw new InvalidOperationException("Serve Windows x64.");
            string dir=Path.Combine(Path.GetTempPath(),"RCN3-Setup-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var results=new List<string>();
            try {
                if(!HasBus()) {
                    string installer=Extract("ViGEmSetup",Path.Combine(dir,"ViGEmBusSetup.msi"));
                    int code=RunElevated("msiexec.exe","/i \"" + installer + "\" /norestart");
                    results.Add((code==0 || code==3010) ? (code==3010 ? "ViGEmBus installato. Riavvia Windows." : "ViGEmBus: installer terminato.") : "ViGEmBus: codice "+code+".");
                } else results.Add("ViGEmBus già presente.");
                if(Port()==null) {
                    string inf=Extract("SerialInf",Path.Combine(dir,"vcom_driver11.inf"));
                    Extract("SerialCat",Path.Combine(dir,"vcom_driver.cat"));
                    int code=RunElevated("pnputil.exe","/add-driver \""+inf+"\" /install");
                    results.Add(code==0 ? "Driver USB DJI aggiunto. Collega il radiocomando acceso." :
                        "Driver USB DJI: codice "+code+".");
                } else results.Add("Porta USB DJI già disponibile.");
                return String.Join(Environment.NewLine,results);
            } finally {
                foreach(var path in Directory.GetFiles(dir)) File.Delete(path);
                Directory.Delete(dir);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Report {
        public ushort Buttons;
        public byte LT,RT;
        public short LX,LY,RX,RY;
    }

    sealed class VirtualPad : IControllerOutput {
        const uint Success=0x20000000;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Allocate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Release(IntPtr a);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint Connect(IntPtr a);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint TargetOp(IntPtr a,IntPtr b);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint UpdateOp(IntPtr a,IntPtr b,Report report);
        [DllImport("kernel32",CharSet=CharSet.Unicode,SetLastError=true)]
        static extern IntPtr LoadLibraryEx(string path, IntPtr file,uint flags);
        [DllImport("kernel32",CharSet=CharSet.Ansi,ExactSpelling=true)]
        static extern IntPtr GetProcAddress(IntPtr module,string name);
        [DllImport("kernel32")] static extern bool FreeLibrary(IntPtr module);
        IntPtr dll, client, target;
        bool connected, added;
        string path,dir;
        UpdateOp update;

        T Bind<T>(string name) where T:class {
            var address=GetProcAddress(dll,name);
            if(address==IntPtr.Zero) throw new EntryPointNotFoundException(name);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(address,typeof(T));
        }

        public VirtualPad() {
            try {
                dir=Path.Combine(Path.GetTempPath(),"RCN3-Pad-"+Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                path=Setup.Extract("ViGEmClient",Path.Combine(dir,"ViGEmClient.dll"));
                dll=LoadLibraryEx(path,IntPtr.Zero,0x00000900);
                if(dll==IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                client=Bind<Allocate>("vigem_alloc")();
                if(client==IntPtr.Zero) throw new IOException("ViGEmClient non disponibile.");
                Check(Bind<Connect>("vigem_connect")(client),"Connessione a ViGEmBus");
                connected=true;
                target=Bind<Allocate>("vigem_target_x360_alloc")();
                if(target==IntPtr.Zero) throw new IOException("Impossibile allocare il gamepad.");
                Check(Bind<TargetOp>("vigem_target_add")(client,target),"Creazione Xbox 360 virtuale");
                added=true;
                update=Bind<UpdateOp>("vigem_target_x360_update");
            } catch { Dispose(); throw; }
        }

        static void Check(uint result,string action) {
            if(result!=Success) throw new IOException(action+" (0x"+result.ToString("X8")+").");
        }

        public void Send(Report report,int rcButtons,int wheel) { Check(update(client,target,report),"Aggiornamento gamepad"); }

        public void Dispose() {
            if(added) {
                if(update!=null) update(client,target,new Report());
                Bind<TargetOp>("vigem_target_remove")(client,target);
            }
            if(target!=IntPtr.Zero) Bind<Release>("vigem_target_free")(target);
            if(connected) Bind<Release>("vigem_disconnect")(client);
            if(client!=IntPtr.Zero) Bind<Release>("vigem_free")(client);
            if(dll!=IntPtr.Zero) FreeLibrary(dll);
            try {
                if(path!=null) File.Delete(path);
                if(dir!=null && Directory.Exists(dir)) Directory.Delete(dir);
            } catch(IOException) { } catch(UnauthorizedAccessException) { }
        }
    }

    static class Duml {
        static int Crc(IList<byte> data,int count,int seed,int polynomial) {
            int value=seed;
            for(int i=0;i<count;i++) {
                value^=data[i];
                for(int bit=0;bit<8;bit++) value=(value>>1)^((value&1)==0?0:polynomial);
            }
            return value;
        }
        public static byte[] Request(int sequence,byte command,byte[] payload) {
            int length=13+payload.Length;
            var data=new byte[length];
            data[0]=0x55; data[1]=(byte)length; data[2]=(byte)(4|(length>>8));
            data[3]=(byte)Crc(data,3,0x77,0x8C);
            data[4]=0x0a; data[5]=0x06; data[6]=(byte)sequence; data[7]=(byte)(sequence>>8);
            data[8]=0x40; data[9]=0x06; data[10]=command;
            payload.CopyTo(data,11);
            int crc=Crc(data,length-2,0x3692,0x8408);
            data[length-2]=(byte)crc; data[length-1]=(byte)(crc>>8);
            return data;
        }
        public static byte[] Frame(List<byte> buffer) {
            while(buffer.Count>=4) {
                int start=buffer.IndexOf(0x55);
                if(start<0) { buffer.Clear(); return null; }
                if(start>0) buffer.RemoveRange(0,start);
                if(buffer.Count<4) break;
                int length=buffer[1]+((buffer[2]&3)<<8);
                if((buffer[2]>>2)!=1 || length<13 || Crc(buffer,3,0x77,0x8C)!=buffer[3]) {
                    buffer.RemoveAt(0); continue;
                }
                if(buffer.Count<length) break;
                if(Crc(buffer,length-2,0x3692,0x8408)!=(buffer[length-2]|(buffer[length-1]<<8))) {
                    buffer.RemoveAt(0); continue;
                }
                var frame=buffer.GetRange(0,length).ToArray();
                buffer.RemoveRange(0,length);
                return frame;
            }
            return null;
        }
    }

    class Options {
        public bool Enabled, Calibrating, NeutralOutput;
        public string ForcedPort;
        public int TimingRun;
        public Profile Profile;
        public Dictionary<int,Profile> SwitchProfiles;
    }

    class Sample {
        public string Status;
        public int[] Raw;
        public short[] Output;
        public double Hz, P95;
        public int Rate, Stalls, TimingRun;
        public OutputMode Mode;
        public ushort Buttons;
        public int PhysicalButtons;
        public int? SwitchCode;
        public string ActiveProfile;
        public byte LeftTrigger, RightTrigger;
    }

    sealed class Bridge : IDisposable {
        static readonly byte[] Empty=new byte[0];
        readonly ManualResetEvent stopping=new ManualResetEvent(false);
        readonly Thread thread;
        public volatile Options Options;
        public volatile Sample Latest;
        public readonly System.Collections.Concurrent.ConcurrentQueue<int[]> Calibration =
            new System.Collections.Concurrent.ConcurrentQueue<int[]>();

        public Bridge(Options options) {
            Options=options;
            thread=new Thread(Work) {IsBackground=true,Name="DJI RC-N3 serial"};
            thread.Start();
        }

        void Status(string message) { Latest=new Sample {Status=message}; }

        void Work() {
            while(!stopping.WaitOne(0)) {
                var options=Options;
                try {
                    if(!options.Enabled) Status("Supporto disattivato");
                    else {
                        string port=options.ForcedPort ?? Setup.Port();
                        if(port==null) Status("In attesa del DJI RC-N3 USB acceso");
                        else Run(port);
                    }
                } catch(Exception ex) {
                    Status(ex.Message);
                    if(stopping.WaitOne(900)) break;
                    continue;
                }
                if(stopping.WaitOne(300)) break;
            }
        }

        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint period);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint period);

        void Run(string portName) {
            bool timer = timeBeginPeriod(1) == 0;
            try { RunSerial(portName); }
            finally { if(timer) timeEndPeriod(1); }
        }

        void RunSerial(string portName) {
            using(var serial=new SerialPort(portName,115200,Parity.None,8,StopBits.One)) {
                serial.ReadTimeout=8;
                serial.WriteTimeout=220;
                serial.Open();
                serial.DiscardInBuffer();
                int sequence=0;
                byte[] initial=Duml.Request(sequence++,0x24,new byte[]{1});
                serial.Write(initial,0,initial.Length);
                var buffer=new List<byte>();
                byte[] bytes=new byte[512];
                var clock=Stopwatch.StartNew();
                double last=0, nextPoll=0, nextButtons=0, nextUi=0, lastButtons=-1000;
                double nextStats=0, hz=0, p95=0;
                var timing=new FrameTiming();
                var pendingSticks=new Dictionary<ushort,double>();
                var expiredRequests=new List<ushort>(2);
                int previousRate=0, previousTimingRun=0, stalls=0;
                ushort buttons=0;
                IControllerOutput pad=null;
                OutputMode? activeMode=null;
                string padError=null;
                double lastTry=-3000;
                Status("Apertura protocollo su "+portName);
                try {
                    while(!stopping.WaitOne(0)) {
                        var options=Options;
                        if(!options.Enabled || (options.ForcedPort!=null && options.ForcedPort!=portName)) break;
                        var profile=options.Profile;
                        Profile switched;
                        if(options.SwitchProfiles!=null && clock.Elapsed.TotalMilliseconds-lastButtons<250 &&
                           options.SwitchProfiles.TryGetValue(ButtonBindings.SwitchCode(buttons),out switched))
                            profile=switched;
                        double now=clock.ElapsedTicks * 1000.0 / Stopwatch.Frequency;
                        if(activeMode!=profile.Output) {
                            if(pad!=null) { pad.Dispose(); pad=null; }
                            activeMode=profile.Output;
                            padError=null;
                            lastTry=now-3000;
                        }
                        if(profile.Rate!=previousRate || options.TimingRun!=previousTimingRun) {
                            previousRate=profile.Rate; previousTimingRun=options.TimingRun;
                            nextPoll=now; nextStats=now;
                            timing=new FrameTiming(); hz=0; p95=0; stalls=0; pendingSticks.Clear();
                        }
                        if(now-last>=850) { Status("Telemetria assente su "+portName+" · riconnessione"); return; }
                        expiredRequests.Clear();
                        foreach(var item in pendingSticks)
                            if(now-item.Value>=45) expiredRequests.Add(item.Key);
                        foreach(var item in expiredRequests) pendingSticks.Remove(item);
                        stalls+=expiredRequests.Count;

                        if(now>=nextPoll && pendingSticks.Count<2) {
                            ushort requestId=(ushort)sequence++;
                            byte[] request=Duml.Request(requestId,1,Empty);
                            serial.Write(request,0,request.Length);
                            pendingSticks[requestId]=now;
                            double interval=1000.0/profile.Rate;
                            nextPoll=NextPoll(nextPoll,now,interval);
                        }
                        if(now>=nextButtons) {
                            byte[] request=Duml.Request(sequence++,0x27,Empty);
                            serial.Write(request,0,request.Length);
                            nextButtons=now+20;
                        }
                        int pending=serial.BytesToRead;
                        if(pending==0) { stopping.WaitOne(1); continue; }
                        int count;
                        try { count=serial.Read(bytes,0,Math.Min(bytes.Length,pending)); }
                        catch(TimeoutException) { continue; }
                        for(int i=0;i<count;i++) buffer.Add(bytes[i]);
                        byte[] frame;
                        while((frame=Duml.Frame(buffer))!=null) {
                            if(frame[9]!=6) continue;
                            if(frame.Length==58 && frame[10]==0x27) {
                                buttons=(ushort)(frame[28]<<8|frame[29]); lastButtons=now;
                            }
                            if(frame.Length!=38 || frame[10]!=1) continue;
                            pendingSticks.Remove((ushort)Word(frame,6));
                            now=clock.ElapsedTicks * 1000.0 / Stopwatch.Frequency;
                            last=now;
                            timing.Record(now);
                            int[] raw={ Word(frame,22),Word(frame,19),Word(frame,13),Word(frame,16) };
                            if(options.Calibrating) Calibration.Enqueue(raw);
                            short[] values=profile.Axes.Select(a=>a.Apply(raw[a.Input])).ToArray();
                            int pressed=profile.Buttons && now-lastButtons<250 ? ButtonBindings.Pressed(buttons) : 0;
                            int wheel=profile.Wheel ? Math.Max(-255,Math.Min(255,(Word(frame,25)-1024)*255/660)):0;
                            var report=new Report {
                                LX=values[0], LY=values[1], RX=values[2], RY=values[3],
                                LT=(byte)Math.Max(0,-wheel), RT=(byte)Math.Max(0,wheel)
                            };
                            profile.Bindings.Apply(pressed,ref report);
                            if(pad==null && now-lastTry>=3000) {
                                lastTry=now;
                                try {
                                    pad=profile.Output==OutputMode.GenericRc
                                        ? (IControllerOutput)new VirtualRc() : new VirtualPad();
                                    padError=null;
                                }
                                catch(Exception ex) { padError=ex.Message; }
                            }
                            bool neutral=options.Calibrating || options.NeutralOutput;
                            if(neutral) report=new Report();
                            if(pad!=null) pad.Send(report,neutral ? 0 : pressed,neutral ? 0 : wheel);
                            if(now>=nextUi) {
                                if(now>=nextStats) {
                                    hz=timing.Rate; p95=timing.P95; nextStats=now+200;
                                }
                                string outputName=profile.Output==OutputMode.GenericRc ? "RC generico (vJoy)" : "Xbox 360 virtuale";
                                Latest=new Sample { Status=(padError ?? (outputName+" attivo · "+portName)),
                                    Mode=profile.Output,
                                    Raw=raw, Output=options.Calibrating || options.NeutralOutput ? new short[4] : values,
                                    Hz=hz, P95=p95, Rate=profile.Rate, Stalls=stalls, TimingRun=options.TimingRun,
                                    Buttons=report.Buttons, PhysicalButtons=pressed, LeftTrigger=report.LT, RightTrigger=report.RT,
                                    SwitchCode=now-lastButtons<250 ? (int?)ButtonBindings.SwitchCode(buttons) : null,
                                    ActiveProfile=profile.Name };
                                nextUi=now+40;
                            }
                        }
                    }
                } finally { if(pad!=null) pad.Dispose(); }
            }
        }

        static int Word(byte[] frame,int offset) { return frame[offset]+(frame[offset+1]<<8); }
        internal static double NextPoll(double previous, double now, double interval) {
            double next=previous+interval;
            return next>now ? next : now+interval;
        }

        public void Dispose() {
            stopping.Set();
            if(thread.Join(1600)) stopping.Dispose();
        }
    }
}

