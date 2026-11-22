using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace RcN3 {
    public enum OutputMode { Xbox360, GenericRc }

    interface IControllerOutput : IDisposable {
        void Send(Report report, int rcButtons, int wheel);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HidPosition {
        public byte Device;
        public int Throttle, Rudder, Aileron, X, Y, Z, Rx, Ry, Rz, Slider, Dial, Wheel;
        public int Vx, Vy, Vz, VbrX, VbrY, VbrZ;
        public uint Buttons, Hat, Hat1, Hat2, Hat3, Buttons1, Buttons2, Buttons3;
    }

    sealed class VirtualRc : IControllerOutput {
        public const string DriverUrl="https://github.com/SciFi-Bob/vJoy_uni/releases";
        const uint X=0x30, Y=0x31, Z=0x32, Rx=0x33, Ry=0x34, Rz=0x35, Slider=0x36, Dial=0x37, Wheel=0x38;
        static readonly uint[] Usages={X,Y,Z,Rx,Ry,Rz,Slider,Dial,Wheel};
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Enabled();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Match(out ushort client, out ushort driver);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DeviceOp(uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Release(uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int HasAxis(uint id,uint usage);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int AxisBound(uint id,uint usage,out int value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Update(uint id, ref HidPosition state);
        [DllImport("kernel32",CharSet=CharSet.Unicode,SetLastError=true)]
        static extern IntPtr LoadLibraryEx(string path,IntPtr reserved,uint flags);
        [DllImport("kernel32",CharSet=CharSet.Ansi,ExactSpelling=true)]
        static extern IntPtr GetProcAddress(IntPtr module,string name);
        [DllImport("kernel32")] static extern bool FreeLibrary(IntPtr module);
        readonly int[] minima=new int[Usages.Length], maxima=new int[Usages.Length];
        IntPtr module;
        uint device;
        Update update;
        Release release;

        public VirtualRc() {
            string[] roots={Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)};
            string path=roots.Where(root=>!String.IsNullOrEmpty(root)).Distinct(StringComparer.OrdinalIgnoreCase)
                .SelectMany(root=>new[]{Path.Combine(root,"vJoy","x64","vJoyInterface.dll"),
                    Path.Combine(root,"vJoy","vJoyInterface.dll")}).FirstOrDefault(File.Exists);
            if(path==null) throw new IOException("RC generico: serve vJoy x64. Apri Driver RC.");
            module=LoadLibraryEx(path,IntPtr.Zero,0x00000900);
            if(module==IntPtr.Zero)
                throw new IOException("RC generico: impossibile caricare vJoyInterface.dll x64 ("+Marshal.GetLastWin32Error()+").");
            try {
                ushort client, driver;
                if(Bind<Enabled>("vJoyEnabled")()==0 ||
                    Bind<Match>("DriverMatch")(out client,out driver)==0)
                    throw new IOException("RC generico: driver vJoy e DLL devono essere attivi e della stessa versione.");
                var status=Bind<DeviceOp>("GetVJDStatus");
                var acquire=Bind<DeviceOp>("AcquireVJD");
                var buttons=Bind<DeviceOp>("GetVJDButtonNumber");
                var exists=Bind<HasAxis>("GetVJDAxisExist");
                var min=Bind<AxisBound>("GetVJDAxisMin");
                var max=Bind<AxisBound>("GetVJDAxisMax");
                update=Bind<Update>("UpdateVJD");
                release=Bind<Release>("RelinquishVJD");
                for(uint id=1;id<=16;id++) {
                    if(status(id)!=1 || buttons(id)<4 || new[]{X,Y,Rx,Ry}.Any(axis=>exists(id,axis)==0))
                        continue;
                    if(acquire(id)==0) continue;
                    device=id;
                    break;
                }
                if(device==0)
                    throw new IOException("RC generico: configura un dispositivo vJoy libero con X, Y, Rx, Ry e 4 tasti.");
                for(int i=0;i<Usages.Length;i++) {
                    int low,high;
                    if(min(device,Usages[i],out low)!=0 && max(device,Usages[i],out high)!=0 && high>low) {
                        minima[i]=low;
                        maxima[i]=high;
                    } else if(i==0 || i==1 || i==3 || i==4) {
                        throw new IOException("RC generico: intervallo assi vJoy non disponibile.");
                    }
                }
                Send(new Report(),0,0);
            } catch { Dispose(); throw; }
        }

        T Bind<T>(string name) where T:class {
            IntPtr address=GetProcAddress(module,name);
            if(address==IntPtr.Zero) throw new IOException("API vJoy mancante: "+name);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(address,typeof(T));
        }

        internal static int Scale(short value,int min,int max) {
            return (int)Math.Round(min+((double)max-min)*((value+32768)/65535.0));
        }

        internal static HidPosition Position(byte id,Report report,int rcButtons,int wheel,int[] min,int[] max) {
            return new HidPosition {
                Device=id,
                X=Scale(report.LX,min[0],max[0]),Y=Scale(report.LY,min[1],max[1]),
                Z=Scale(0,min[2],max[2]),
                Rx=Scale(report.RX,min[3],max[3]),Ry=Scale(report.RY,min[4],max[4]),
                Rz=Scale((short)(wheel*128),min[5],max[5]),
                Slider=Scale(0,min[6],max[6]),Dial=Scale(0,min[7],max[7]),Wheel=Scale(0,min[8],max[8]),
                Buttons=(uint)(rcButtons&0x0F),
                Hat=uint.MaxValue,Hat1=uint.MaxValue,Hat2=uint.MaxValue,Hat3=uint.MaxValue
            };
        }

        public void Send(Report report,int rcButtons,int wheel) {
            var state=Position((byte)device,report,rcButtons,wheel,minima,maxima);
            if(update(device,ref state)==0) throw new IOException("RC generico: aggiornamento vJoy non riuscito.");
        }

        public void Dispose() {
            try {
                if(device!=0) {
                    var neutral=Position((byte)device,new Report(),0,0,minima,maxima);
                    try { update(device,ref neutral); }
                    finally { release(device); device=0; }
                }
            } finally {
                if(module!=IntPtr.Zero) { FreeLibrary(module); module=IntPtr.Zero; }
            }
        }
    }
}

