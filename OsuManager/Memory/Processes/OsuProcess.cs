﻿using osu.Memory.Processes.Enums;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using static osu.Memory.Processes.MemoryBasicInformation;

namespace osu.Memory.Processes
{
    public class OsuProcess
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, UIntPtr lpBaseAddress, [Out] byte[] lpBuffer, uint dwSize, out UIntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteProcessMemory(IntPtr hProcess, UIntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out UIntPtr lpNumberOfBytesWritten);

        //TODO: x64 support
        [DllImport("kernel32.dll")]
        public static extern int VirtualQueryEx(IntPtr hProcess, UIntPtr lpAddress, out MEMORY_BASIC_INFORMATION_32 lpBuffer, uint dwLength);

        public Process Process { get; private set; }

        public OsuProcess(Process process) => Process = process;

        public bool FindPattern(string pattern, out UIntPtr result)
        {
            PatternByte[] patternBytes = parsePattern(pattern);

            var regions = EnumerateMemoryRegions();
            foreach (var region in regions)
            {
                if ((uint)region.BaseAddress < (uint)Process.MainModule.BaseAddress)
                    continue;

                byte[] buffer = ReadMemory(region.BaseAddress, region.RegionSize.ToUInt32());
                if (findMatch(patternBytes, buffer, out UIntPtr match))
                {
                    result = (UIntPtr)(region.BaseAddress.ToUInt32() + match.ToUInt32());
                    return true;
                }
            }

            result = UIntPtr.Zero;
            return false;
        }

        public List<MemoryRegion> EnumerateMemoryRegions()
        {
            var regions = new List<MemoryRegion>();
            UIntPtr address = UIntPtr.Zero;

            while (VirtualQueryEx(Process.Handle, address, out MEMORY_BASIC_INFORMATION_32 basicInformation, (uint)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION_32))) != 0)
            {
                if (basicInformation.State != MemoryState.MemFree && !basicInformation.Protect.HasFlag(MemoryProtect.PageGuard))
                    regions.Add(new MemoryRegion(basicInformation));

                address = (UIntPtr)(basicInformation.BaseAddress.ToUInt32() + basicInformation.RegionSize.ToUInt32());
            }

            return regions;
        }

        public byte[] ReadMemory(UIntPtr address, uint size)
        {
            byte[] result = new byte[size];
            ReadProcessMemory(Process.Handle, address, result, size, out UIntPtr bytesRead);
            return result;
        }

        public UIntPtr ReadMemory(UIntPtr address, byte[] buffer, uint size)
        {
            UIntPtr bytesRead;
            ReadProcessMemory(Process.Handle, address, buffer, size, out bytesRead);
            return bytesRead;
        }

        public void WriteMemory(UIntPtr address, byte[] data, uint length)
        {
            WriteProcessMemory(Process.Handle, address, data, length, out UIntPtr bytesWritten);
        }

        public int ReadInt32(UIntPtr address) => BitConverter.ToInt32(ReadMemory(address, sizeof(int)), 0);

        public uint ReadUInt32(UIntPtr address) => BitConverter.ToUInt32(ReadMemory(address, sizeof(uint)), 0);

        public long ReadInt64(UIntPtr address) => BitConverter.ToInt64(ReadMemory(address, sizeof(long)), 0);

        public ulong ReadUInt64(UIntPtr address) => BitConverter.ToUInt64(ReadMemory(address, sizeof(ulong)), 0);

        public float ReadFloat(UIntPtr address) => BitConverter.ToSingle(ReadMemory(address, sizeof(float)), 0);

        public double ReadDouble(UIntPtr address) => BitConverter.ToDouble(ReadMemory(address, sizeof(double)), 0);

        public bool ReadBool(UIntPtr address) => BitConverter.ToBoolean(ReadMemory(address, sizeof(bool)), 0);

        public string ReadString(UIntPtr address, bool multiply = false, Encoding encoding = null)
        {
            encoding = encoding ?? Encoding.UTF8;
            UIntPtr stringAddress = (UIntPtr)ReadUInt32(address);
            int length = ReadInt32(stringAddress + 0x4) * (multiply ? 2 : 1);

            return encoding.GetString(ReadMemory(stringAddress + 0x8, (uint)length)).Replace("\0", string.Empty);
        }

        private PatternByte[] parsePattern(string pattern)
        {
            PatternByte[] patternBytes = new PatternByte[pattern.Split(' ').Length];
            for (int i = 0; i < patternBytes.Length; i++)
            {
                string currentByte = pattern.Split(' ')[i];

                patternBytes[i] = currentByte == "??" ? new PatternByte(0x0, true) : new PatternByte(Convert.ToByte(currentByte, 16));
            }

            return patternBytes;
        }

        private bool findMatch(PatternByte[] pattern, byte[] buffer, out UIntPtr result)
        {
            result = UIntPtr.Zero;

            for (int i = 0; i + pattern.Length <= buffer.Length; i++)
            {
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (!pattern[j].Matches(buffer[i + j]))
                        break;

                    if (j == pattern.Length - 1)
                    {
                        result = (UIntPtr)i;
                        return true;
                    }
                }
            }

            return false;
        }
    }
}