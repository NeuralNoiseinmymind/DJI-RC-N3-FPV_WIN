using System;
using System.Collections.Generic;
using System.Linq;

namespace RcN3 {
    public enum XboxControl {
        None, A, B, X, Y, LB, RB, LT, RT, Back, Start, LS, RS, Up, Down, Left, Right
    }

    public sealed class ButtonBindings {
        static readonly int[] Masks = { 0x0060, 0x0080, 0x0004, 0x0002 };
        public static readonly string[] Sources = { "Foto / Video", "Pausa / RTH", "Scatto / Registra", "Fn" };
        public XboxControl Photo = XboxControl.A;
        public XboxControl Rth = XboxControl.B;
        public XboxControl Camera = XboxControl.X;
        public XboxControl Function = XboxControl.Y;

        public XboxControl[] Values {
            get { return new[] { Photo, Rth, Camera, Function }; }
        }

        public static int SwitchCode(ushort packet) { return (packet >> 12) & 3; }

        public static int Pressed(ushort packet) {
            int result = 0;
            for (int i = 0; i < Masks.Length; i++)
                if ((packet & Masks[i]) == Masks[i]) result |= 1 << i;
            return result;
        }

        static ushort Bits(XboxControl control) {
            switch (control) {
                case XboxControl.Up: return 0x0001;
                case XboxControl.Down: return 0x0002;
                case XboxControl.Left: return 0x0004;
                case XboxControl.Right: return 0x0008;
                case XboxControl.Start: return 0x0010;
                case XboxControl.Back: return 0x0020;
                case XboxControl.LS: return 0x0040;
                case XboxControl.RS: return 0x0080;
                case XboxControl.LB: return 0x0100;
                case XboxControl.RB: return 0x0200;
                case XboxControl.A: return 0x1000;
                case XboxControl.B: return 0x2000;
                case XboxControl.X: return 0x4000;
                case XboxControl.Y: return 0x8000;
                default: return 0;
            }
        }

        internal void Apply(int pressed, ref Report report) {
            for (int i = 0; i < Masks.Length; i++) {
                if ((pressed & (1 << i)) == 0) continue;
                XboxControl control;
                switch (i) {
                    case 0: control = Photo; break;
                    case 1: control = Rth; break;
                    case 2: control = Camera; break;
                    default: control = Function; break;
                }
                if (control == XboxControl.LT) report.LT = 255;
                else if (control == XboxControl.RT) report.RT = 255;
                else report.Buttons |= Bits(control);
            }
        }
    }

    internal static class UsbRates {
        public static readonly int[] Supported = { 25, 50, 100, 125, 150, 200, 250 };
    }

    internal sealed class FrameTiming {
        readonly Queue<double> intervals = new Queue<double>();
        double last = -1;
        double sum;

        public void Record(double milliseconds) {
            if (last >= 0) {
                double interval = milliseconds - last;
                intervals.Enqueue(interval);
                sum += interval;
                if (intervals.Count > 128) sum -= intervals.Dequeue();
            }
            last = milliseconds;
        }

        public double Rate { get { return sum > 0 ? intervals.Count * 1000.0 / sum : 0; } }
        public double P95 {
            get {
                if (intervals.Count == 0) return 0;
                var sorted = intervals.OrderBy(x => x).ToArray();
                return sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1];
            }
        }
    }
}

