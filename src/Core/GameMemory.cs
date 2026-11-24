using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WitchKingTrainer.Core
{
    public enum AttachResult { Ok, NotRunning, AccessDenied, WrongVersion, Failed }

    /// <summary>Handle to the running game process with typed read/write helpers. All reads fail soft (return false / 0).</summary>
    public sealed class GameMemory : IDisposable
    {
        IntPtr _h = IntPtr.Zero;
        public int ProcessId { get; private set; }
        public string ProcessName { get; private set; }
        public string ImagePath { get; private set; }
        public bool IsOpen { get { return _h != IntPtr.Zero; } }
        internal IntPtr Handle { get { return _h; } }

        /// <summary>Finds the game process. The 2.02 delay-fix build runs as lotrbfme2ep1.exe; the classic launcher runs game.dat.</summary>
        /// <summary>Every process that could be one of the supported games (newest first). The caller verifies each one.</summary>
        public static System.Collections.Generic.List<Process> FindGameProcesses()
        {
            var res = new System.Collections.Generic.List<Process>();
            foreach (string name in new[] { "lotrbfme2ep1", "game.dat", "lotrbfme2" })
            {
                Process[] list; try { list = Process.GetProcessesByName(name); } catch { continue; }
                foreach (Process p in list) { try { if (!p.HasExited) res.Add(p); } catch { res.Add(p); } }
            }
            res.Sort((a, b) => { try { return b.StartTime.CompareTo(a.StartTime); } catch { return 0; } });
            return res;
        }

        public static Process FindGameProcess()
        {
            foreach (string name in new[] { "lotrbfme2ep1", "game.dat" })
            {
                Process[] list;
                try { list = Process.GetProcessesByName(name); } catch { continue; }
                Process best = null;
                foreach (Process p in list)
                {
                    // the launcher stub exits right after spawning the elevated game, so prefer the newest instance with a window
                    try { if (p.HasExited) continue; } catch { }
                    if (best == null) best = p;
                    else { try { if (p.StartTime > best.StartTime) best = p; } catch { } }
                }
                if (best != null) return best;
            }
            return null;
        }

        public AttachResult Open(Process p, out string error)
        {
            Close(); error = null;
            if (p == null) return AttachResult.NotRunning;
            uint access = Native.PROCESS_VM_OPERATION | Native.PROCESS_VM_READ | Native.PROCESS_VM_WRITE | Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_SUSPEND_RESUME | Native.SYNCHRONIZE;
            IntPtr h = Native.OpenProcess(access, false, p.Id);
            if (h == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                error = new Win32Exception(err).Message;
                return err == Native.ERROR_ACCESS_DENIED ? AttachResult.AccessDenied : AttachResult.Failed;
            }
            _h = h; ProcessId = p.Id;
            try { ProcessName = p.ProcessName; } catch { ProcessName = "?"; }
            var sb = new StringBuilder(1024); int n = sb.Capacity;
            ImagePath = Native.QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : "";
            return AttachResult.Ok;
        }

        /// <summary>SizeOfImage from the PE header of the main executable (0 if unreadable). The game engines are over 8 MB; launcher stubs are tiny.</summary>
        public uint ImageSize()
        {
            byte[] h = ReadBytes(0x00400000, 0x400); if (h == null || h[0] != (byte)'M') return 0; int pe = BitConverter.ToInt32(h, 0x3C);
            return pe > 0 && pe < 0x300 ? BitConverter.ToUInt32(h, pe + 0x50) : 0;
        }

        public bool HasExited { get { return _h == IntPtr.Zero || Native.WaitForSingleObject(_h, 0) == 0; } }

        public void Close() { if (_h != IntPtr.Zero) { Native.CloseHandle(_h); _h = IntPtr.Zero; } ProcessId = 0; }
        public void Dispose() { Close(); }

        // ---- reads ----
        public bool ReadBytes(uint addr, byte[] buf, int count)
        {
            if (_h == IntPtr.Zero || addr < 0x10000) return false;
            int rd; return Native.ReadProcessMemory(_h, Native.Ptr(addr), buf, count, out rd) && rd == count;
        }
        public byte[] ReadBytes(uint addr, int count) { var b = new byte[count]; return ReadBytes(addr, b, count) ? b : null; }
        public bool TryU32(uint addr, out uint v) { var b = new byte[4]; if (ReadBytes(addr, b, 4)) { v = BitConverter.ToUInt32(b, 0); return true; } v = 0; return false; }
        public uint U32(uint addr) { uint v; TryU32(addr, out v); return v; }
        public int I32(uint addr) { return unchecked((int)U32(addr)); }
        public bool TryF32(uint addr, out float v) { var b = new byte[4]; if (ReadBytes(addr, b, 4)) { v = BitConverter.ToSingle(b, 0); return true; } v = 0; return false; }
        public float F32(uint addr) { float v; TryF32(addr, out v); return v; }
        public string AsciiZ(uint addr, int max)
        {
            var b = ReadBytes(addr, max); if (b == null) { b = ReadBytes(addr, 16); if (b == null) return null; }
            int n = 0; while (n < b.Length && b[n] != 0) { if (b[n] < 0x20 || b[n] > 0x7E) return null; n++; }
            return n == b.Length ? null : Encoding.ASCII.GetString(b, 0, n);
        }

        // ---- writes ----
        public bool WriteBytes(uint addr, byte[] data)
        {
            if (_h == IntPtr.Zero || addr < 0x10000) return false;
            int wr; return Native.WriteProcessMemory(_h, Native.Ptr(addr), data, data.Length, out wr) && wr == data.Length;
        }
        public bool WriteU32(uint addr, uint v) { return WriteBytes(addr, BitConverter.GetBytes(v)); }
        public bool WriteI32(uint addr, int v) { return WriteBytes(addr, BitConverter.GetBytes(v)); }
        public bool WriteF32(uint addr, float v) { return WriteBytes(addr, BitConverter.GetBytes(v)); }

        /// <summary>Writes into a code page: temporarily makes it writable, restores protection and flushes the instruction cache.</summary>
        public bool WriteCode(uint addr, byte[] data)
        {
            if (_h == IntPtr.Zero) return false;
            uint old, tmp;
            if (!Native.VirtualProtectEx(_h, Native.Ptr(addr), (IntPtr)data.Length, Native.PAGE_EXECUTE_READWRITE, out old)) return false;
            bool ok = WriteBytes(addr, data);
            Native.VirtualProtectEx(_h, Native.Ptr(addr), (IntPtr)data.Length, old, out tmp);
            Native.FlushInstructionCache(_h, Native.Ptr(addr), (IntPtr)data.Length);
            return ok;
        }

        public uint Alloc(int size, bool executable)
        {
            if (_h == IntPtr.Zero) return 0;
            IntPtr p = Native.VirtualAllocEx(_h, IntPtr.Zero, (IntPtr)size, Native.MEM_COMMIT | Native.MEM_RESERVE, executable ? Native.PAGE_EXECUTE_READWRITE : Native.PAGE_READWRITE);
            return unchecked((uint)p.ToInt64());
        }

        public void Suspend() { if (_h != IntPtr.Zero) Native.NtSuspendProcess(_h); }
        public void Resume() { if (_h != IntPtr.Zero) Native.NtResumeProcess(_h); }

        /// <summary>Enumerates committed private regions (used to rediscover the trainer's own data page after a restart).</summary>
        internal System.Collections.Generic.IEnumerable<Native.MEMORY_BASIC_INFORMATION> PrivateRegions()
        {
            long a = 0x10000; Native.MEMORY_BASIC_INFORMATION m; int sz = Marshal.SizeOf(typeof(Native.MEMORY_BASIC_INFORMATION));
            while (a < 0xFFFF0000L && _h != IntPtr.Zero && Native.VirtualQueryEx(_h, Native.Ptr((uint)a), out m, sz) != 0)
            {
                long size = (long)(ulong)(uint)m.RegionSize.ToInt64(); if (size == 0) break;
                if (m.State == Native.MEM_COMMIT && m.Type == Native.MEM_PRIVATE) yield return m;
                a = (long)(uint)m.BaseAddress.ToInt64() + size;
            }
        }
    }
}
