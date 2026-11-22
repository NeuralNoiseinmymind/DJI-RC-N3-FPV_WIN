using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Serialization;
using System.Runtime.InteropServices;

namespace RcN3 {
    static class SmokeTests {
        static void Assert(bool ok,string label) {
            if (!ok) throw new InvalidOperationException(label);
        }

        static void Main() {
            var bindings = new ButtonBindings();
            Assert(ButtonBindings.SwitchCode(0x2060)==2 && ButtonBindings.Pressed(0x2060)==1 &&
                ButtonBindings.Pressed(0x3000)==0,"Selettore e pulsanti indipendenti.");
            var report = new Report();
            bindings.Apply(ButtonBindings.Pressed(0x10E6), ref report);
            Assert(report.Buttons==0xF000,"I quattro tasti mantengono le assegnazioni precedenti.");
            report=new Report { RT=47 };
            bindings.Photo=XboxControl.LT;
            bindings.Rth=XboxControl.Right;
            bindings.Camera=XboxControl.None;
            bindings.Function=XboxControl.RS;
            bindings.Apply(ButtonBindings.Pressed(0x10E6),ref report);
            Assert(report.Buttons==0x0088 && report.LT==255 && report.RT==47,
                "Mappatura simultanea con trigger, D-pad, stick e azione disattivata.");
            report=new Report();
            bindings.Apply(ButtonBindings.Pressed(0x1000),ref report);
            Assert(report.Buttons==0 && report.LT==0,"Il bit comune non è un tasto premuto.");

            var legacy = "<Settings><Profiles><Profile><Name>V1</Name><Rate>50</Rate></Profile></Profiles></Settings>";
            using(var stream=new MemoryStream(Encoding.UTF8.GetBytes(legacy))) {
                var imported=Files.ReadXml(stream);
                Assert(imported.Profiles[0].Output==OutputMode.Xbox360,"Uscita invariata per i profili precedenti.");
                Assert(imported.Profiles.Count==1 && imported.Profiles[0].Bindings.Function==XboxControl.Y,
                    "Profilo precedente senza mappatura esplicita.");
            }
            using(var stream=new MemoryStream()) {
                var saved=Settings.Defaults();
                saved.Profiles[0].Bindings=bindings;
                saved.Profiles[0].Rate=250;
                saved.Profiles[0].Output=OutputMode.GenericRc;
                new XmlSerializer(typeof(Settings)).Serialize(stream,saved);
                stream.Position=0;
                var savedProfile=Files.ReadXml(stream).Profiles[0];
                Assert(savedProfile.Output==OutputMode.GenericRc,"Uscita RC conservata.");
                Assert(savedProfile.Rate==250,"Persistenza della frequenza sperimentale.");
                var restored=savedProfile.Bindings;
                Assert(restored.Photo==XboxControl.LT && restored.Function==XboxControl.RS,
                    "Persistenza delle mappature.");
            }
            Assert(Marshal.SizeOf(typeof(HidPosition))==108 &&
                Marshal.OffsetOf(typeof(HidPosition),"Buttons").ToInt32()==76,"Layout nativo vJoy.");
            int[] lows=Enumerable.Repeat(1,9).ToArray(), highs=Enumerable.Repeat(32768,9).ToArray();
            var hid=VirtualRc.Position(2,new Report {LX=-32768,LY=32767,RY=32767},0x12,-255,lows,highs);
            Assert(hid.Device==2 && hid.X==1 && hid.Y==32768 && hid.Ry==32768 &&
                hid.Rx==16385 && hid.Rz<100 && hid.Buttons==2 && hid.Hat==uint.MaxValue,
                "Assi e pulsanti HID.");
            var axis=new Axis();
            Assert(axis.Apply(364)==-32768 && axis.Apply(1024)==0 && axis.Apply(1684)==32767,"Estremi assi.");
            axis.Invert=true;
            axis.Expo=70;
            Assert(axis.Apply(364)==32767 && axis.Apply(1030)==0 && axis.Apply(1684)==-32768,
                "Inversione, deadzone ed expo.");
            byte[] packet=Duml.Request(0x34eb,0x24,new byte[]{1});
            Assert(BitConverter.ToString(packet)=="55-0E-04-66-0A-06-EB-34-40-06-24-01-D9-EC","CRC DUML.");
            var buffer=new List<byte>(new byte[]{0,8,9});
            buffer.AddRange(packet.Take(5));
            Assert(Duml.Frame(buffer)==null,"Frame parziale.");
            buffer.AddRange(packet.Skip(5));
            Assert(packet.SequenceEqual(Duml.Frame(buffer)),"Frame completo.");
            var timing=new FrameTiming();
            for(int i=0;i<=128;i++) timing.Record(i*10);
            Assert(timing.Rate==100 && timing.P95==10,"Finestra della telemetria.");
            Assert(Bridge.NextPoll(100,101,10)==110 && Bridge.NextPoll(100,130,10)==140,
                "Scadenza regolare e nessuna raffica dopo uno stallo.");
            Console.WriteLine("PASS: HID, uscita per profilo, pulsanti, profili precedenti, assi, frame USB e telemetria.");
        }
    }
}

