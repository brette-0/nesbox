namespace nesbox;

internal class System {
    internal static class Memory {
        internal static byte Read(int offset) {
            throw new NotImplementedException();
        }
    }
    
    internal static void Initialize() {
        var rng = new Random(byte.MaxValue);

        CPU.RESET = CPU.Assertion.Asserted;
        
        CPU.Register.a.Set((byte)Random.Shared.Next());
        CPU.Register.x.Set((byte)Random.Shared.Next());
        CPU.Register.y.Set((byte)Random.Shared.Next());
        
        CPU.Reset();
    }
    
    internal static class CPU {
        private static void Lifetime() {
            loop:
                SUB = false;
                SubCycle();
                SUB = true;
                SubCycle();
                goto loop;
        }   // infinite loop known. Should change in future
        
        private static void SubCycle() {
            foreach (var latch in new ILatchable[] {
                Register.a, Register.x, Register.y, Register.c,
                Register.z, Register.i, Register.d, Register.b,
                Register.v, Register.n, Register.s,
                Register.IR, Register.PCL, Register.PCH}) {
                latch.DeassertLatch();
            }
            
            switch (RESET, T, SUB) {
                case (Assertion.Asserted, 0, false):
                    RW           = ReadWrite.Read;
                    PHI1         = Assertion.Asserted;
                    PHI2         = Assertion.Deasserted;

                    Register.IR.AssertLatch();
                    Register.IR.Set(0);

                    Register.c.AssertLatch();
                    Register.z.AssertLatch();
                    Register.i.AssertLatch();
                    Register.b.AssertLatch();
                    Register.d.AssertLatch();
                    Register.v.AssertLatch();
                    Register.n.AssertLatch();
                    
                    Register.p = 0x34;
                    
                    Register.s.AssertLatch();
                    Register.s.Set(0xfd);
                    break;
                
                case (Assertion.Asserted, 1, false):
                    Address = Vectors.Reset;
                    break;
                
                case (Assertion.Asserted, 1, true):
                    Register.PCL.AssertLatch();
                    Register.PCL.Set(Memory.Read(Address));
                    break;
                
                case (Assertion.Asserted, 2, false):
                    Address = Vectors.Reset + 1;
                    break;
                
                case (Assertion.Asserted, 2, true):
                    Register.PCH.AssertLatch();
                    Register.PCH.Set(Memory.Read(Address));
                    break;
                
                
                default:
                    break;
            }
        }

        internal static void Reset() {
            thread = new(Lifetime);
            thread.Start();
        }
        
        
        internal static class Register {
            // Registers
            internal static LatchedRegister<byte> a = new();
            internal static LatchedRegister<byte> x = new();
            internal static LatchedRegister<byte> y = new();

            internal static byte p {
                get {
                    byte _p = 0;
                    foreach (var lp in new[] {c, z, i, d, b, v, n}) {
                        var read = lp.Get();
                        
                        _p <<= 1;
                        _p |= (byte)(read is Assertion.Deasserted ? 0 : 1);
                    }

                    return (byte)((_p << 1) & ~0b11 | 0b100 & (_p & 0b11));
                }

                set {
                    n.Set((Assertion)((value >> 7) & 1));
                    v.Set((Assertion)((value >> 6) & 1));
                    b.Set((Assertion)((value >> 5) & 1));
                    d.Set((Assertion)((value >> 4) & 1));
                    i.Set((Assertion)((value >> 2) & 1));
                    z.Set((Assertion)((value >> 1) & 1));
                    c.Set((Assertion)((value >> 0) & 1));
                }
            }

            internal static LatchedRegister<byte> s = new();
            
            internal static LatchedRegister<byte> PCL = new();
            internal static LatchedRegister<byte> PCH = new();
            internal static LatchedRegister<byte> IR  = new();

            // Register latches
            internal static Assertion LA   { get => a.latch; }
            internal static Assertion LX   { get => x.latch; }
            internal static Assertion LY   { get => y.latch; }
            internal static Assertion LS   { get => s.latch; }
            internal static Assertion LPCL { get => PCL.latch; }
            internal static Assertion LPCH { get => PCH.latch; }
            internal static Assertion LIR  { get => IR.latch; }
            
            // Flag latches
            internal static Assertion LC { get => c.latch; }
            internal static Assertion LZ { get => z.latch; }
            internal static Assertion LI { get => i.latch; }
            internal static Assertion LD { get => d.latch; }
            internal static Assertion LB { get => b.latch; }
            internal static Assertion LV { get => v.latch; }
            internal static Assertion LN { get => n.latch; }

            // flags
            internal static LatchedPin c = new();
            internal static LatchedPin z = new();
            internal static LatchedPin i = new();
            internal static LatchedPin d = new();
            internal static LatchedPin b = new();
            internal static LatchedPin v = new();
            internal static LatchedPin n = new();
        }

        internal static class Vectors {
            internal const ushort NMI   = 0xfffa;
            internal const ushort Reset = 0xfffc;
            internal const ushort IRQ   = 0xfffe;
        }

        private const ulong BaseClockSpeed = 1_789_773ul;
        
        private static Thread thread;
        
        internal static class Helper {
            internal static ushort PC { get => (ushort)((Register.PCH.Get() << 8) | Register.PCL.Get()); }

            internal static class PHI {
                internal static void   Flip() => (PHI1, PHI2) = (PHI2, PHI1);
                internal static States Current = (States)(((byte)PHI1 + 2 * (byte)PHI2) % 3);
                
                internal enum States {Error, PHI1, PHI2}
            }
            
            internal static byte p {
                get {
                    byte _p = 0;
                    foreach (var lp in new[] {Register.c, Register.z, Register.i, Register.d, Register.b, Register.v, Register.n}) {
                        var read = lp.Get(true);
                        
                        _p <<= 1;
                        _p |=  (byte)(read is Assertion.Deasserted ? 0 : 1);
                    }

                    return (byte)((_p << 1) & ~0b11 | 0b100 & (_p & 0b11));
                }

                set {
                    Register.n.Set((Assertion)((value >> 7) & 1), true);
                    Register.v.Set((Assertion)((value >> 6) & 1), true);
                    Register.b.Set((Assertion)((value >> 5) & 1), true);
                    Register.d.Set((Assertion)((value >> 4) & 1), true);
                    Register.i.Set((Assertion)((value >> 2) & 1), true);
                    Register.z.Set((Assertion)((value >> 1) & 1), true);
                    Register.c.Set((Assertion)((value >> 0) & 1), true);
                }
            }
        }
        
        internal static byte T   = 0;
        internal static bool SUB = false;
        
        
        #region PINS

        internal static Assertion RESET = Assertion.Deasserted;
        internal static ReadWrite RW    = ReadWrite.Read;
        internal static Assertion PHI1  = Assertion.Deasserted;
        internal static Assertion PHI2  = Assertion.Deasserted;

        internal class LatchedPin : ILatchable {
            internal Assertion Get(bool nocheck = false) => nocheck || latch is Assertion.Asserted 
                ? pin 
                : throw new Exception("pin is latched!");

            internal void Set(Assertion assert, bool nocheck = false) => pin = nocheck || latch is Assertion.Asserted
                ? assert
                : throw new Exception("pin is latched!");
            
            public void AssertLatch()   => latch = Assertion.Asserted;
            public void DeassertLatch() => latch = Assertion.Deasserted;

            internal Assertion pin   { get; private set; }
            internal Assertion latch { get; private set; }
        }

        internal class LatchedRegister<T> : ILatchable where T : IComparable {
            internal LatchedRegister() {
                var t = typeof(T);
                if (t == typeof(uint) || t == typeof(ulong) || t == typeof(byte))
                    value = (T)(object)Random.Shared.Next();
                else throw new NotSupportedException($"{t.FullName} is not supported");
            }
            
            internal T Get(bool nocheck = false) => nocheck || latch is Assertion.Asserted 
                ? value 
                : (T)(object)Random.Shared.Next();
            
            internal void Set(T ctx, bool nocheck = false) => value = nocheck || latch is Assertion.Asserted 
                ? ctx 
                : (T)(object)Random.Shared.Next();


            internal Assertion latch { get; private set; }
            internal T         value { get; private set; }
            
            public void AssertLatch()   => latch = Assertion.Asserted;
            public void DeassertLatch() => latch = Assertion.Deasserted;
        }
        
        private interface ILatchable {
            void AssertLatch();
            void DeassertLatch();
        }

        internal enum ReadWrite : byte { Read, Write }
        internal enum Assertion : byte { Deasserted, Asserted}
        #endregion
        
        #region BUS
        internal static ushort Address = 0;
        #endregion
    }
}