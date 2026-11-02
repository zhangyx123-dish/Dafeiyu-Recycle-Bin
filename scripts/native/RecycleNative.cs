using System;
using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace RecyclePet {
    public sealed class Receipt {
        public bool Success;
        public string OriginalPath = "", RecyclePath = "", ParsingName = "", Error = "";
        public int HResult;
        public string Json() {
            return "{\"time\":" + Native.Quote(DateTime.UtcNow.ToString("o")) + ",\"success\":" + (Success ? "true" : "false")
                + ",\"original\":" + Native.Quote(OriginalPath) + ",\"recycle\":" + Native.Quote(RecyclePath)
                + ",\"parsingName\":" + Native.Quote(ParsingName) + ",\"hresult\":" + HResult
                + ",\"error\":" + Native.Quote(Error) + "}";
        }
    }
    public sealed class ProbeResult {
        public bool Passed, Recycled, Restored;
        public string OriginalPath = "", RecyclePath = "", Error = "";
        public string Json() {
            return "{\"passed\":" + (Passed ? "true" : "false") + ",\"recycled\":" + (Recycled ? "true" : "false")
                + ",\"restored\":" + (Restored ? "true" : "false") + ",\"original\":" + Native.Quote(OriginalPath)
                + ",\"recycle\":" + Native.Quote(RecyclePath) + ",\"error\":" + Native.Quote(Error) + "}";
        }
    }
    public static class Native {
        public static string ProtectedRoot = "";
        public static readonly string StateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecyclePet");
        const uint RecycleFlags = 0x00080000 | 0x20000000 | 0x00100000 | 0x00000400 | 0x00004000 | 0x00000040 | 0x00000010 | 0x00000004;
        const uint MoveFlags = 0x20000000 | 0x00100000 | 0x00000400 | 0x00000010 | 0x00000004;
        public static string Quote(string s) {
            StringBuilder b = new StringBuilder("\"");
            foreach (char c in s ?? "") { if (c == '\\') b.Append("\\\\"); else if (c == '"') b.Append("\\\""); else if (c < 32) b.Append("\\u" + ((int)c).ToString("x4")); else b.Append(c); }
            return b.Append('"').ToString();
        }
        static void WindowsSTA() {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) throw new PlatformNotSupportedException("WINDOWS_REQUIRED: run on the Windows computer containing the files.");
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw new InvalidOperationException("STA_REQUIRED");
        }
        static bool Under(string p, string root) { return !String.IsNullOrEmpty(root) && (p.Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || p.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)); }
        public static string Validate(string raw) {
            WindowsSTA();
            if (String.IsNullOrWhiteSpace(raw) || !Path.IsPathRooted(raw) || raw.StartsWith(@"\\") || raw.IndexOf(':', 2) >= 0) throw new IOException("只接收本地磁盘上的普通文件。");
            string p = Path.GetFullPath(raw);
            if (!File.Exists(p) || Directory.Exists(p)) throw new IOException("文件不存在，或拖入的是文件夹；此版先支持普通文件。");
            DriveInfo drive = new DriveInfo(Path.GetPathRoot(p));
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed || !drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) throw new IOException("此版只接收本地固定 NTFS 磁盘上的文件。");
            foreach (Environment.SpecialFolder f in new [] {Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86})
                if (Under(p, Environment.GetFolderPath(f))) throw new IOException("不处理 Windows 或程序安装目录里的文件。");
            if (Under(p, ProtectedRoot)) throw new IOException("不回收桌宠自身的程序和素材。");
            FileAttributes attributes = File.GetAttributes(p);
            if ((attributes & FileAttributes.System) != 0) throw new IOException("不处理系统文件。");
            // A cloud-managed Desktop is not necessarily a link. Inspect the tag,
            // rather than rejecting every ReparsePoint on the ancestor chain.
            if (!HasLocalData((uint)attributes)) throw new IOException("文件尚未完整保存在本机。请先右键选择‘始终保留在此设备上’，等下载完成后再拖入。");
            string ancestor = p;
            while (!String.IsNullOrEmpty(ancestor)) {
                if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0) {
                    uint tag = ReparseTag(ancestor);
                    if (!IsCloudTag(tag)) throw new IOException("路径包含链接、联接或不支持的重解析点（0x" + tag.ToString("X8") + "）。请将普通文件放到非链接目录后再拖入。");
                }
                ancestor = Path.GetDirectoryName(ancestor);
            }
            return p;
        }
        // Windows IO_REPARSE_TAG_CLOUD and CLOUD_1 ... CLOUD_F. These tags
        // preserve path identity; mount points and symlinks remain rejected.
        public static bool IsCloudTag(uint tag) { return (tag & 0xFFFF0FFFu) == 0x9000001Au; }
        // OFFLINE, RECALL_ON_OPEN, RECALL_ON_DATA_ACCESS: do not trigger hydration.
        public static bool HasLocalData(uint attributes) { return (attributes & (0x1000u | 0x40000u | 0x400000u)) == 0; }
        static uint ReparseTag(string path) {
            using (SafeFileHandle handle = CreateFileW(path, 0, 1 | 2 | 4, IntPtr.Zero, 3, 0x00200000 | 0x02000000, IntPtr.Zero)) {
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法检查路径类型：" + path);
                byte[] buffer = new byte[16384]; uint returned;
                if (!DeviceIoControl(handle, 0x000900A8, IntPtr.Zero, 0, buffer, (uint)buffer.Length, out returned, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取路径类型：" + path);
                if (returned < 8) throw new IOException("路径类型信息不完整。");
                return BitConverter.ToUInt32(buffer, 0);
            }
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, uint inputSize, [Out] byte[] output, uint outputSize, out uint returned, IntPtr overlapped);

        static IShellItem Item(string path) {
            Guid iid = typeof(IShellItem).GUID; IShellItem item;
            int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item); Marshal.ThrowExceptionForHR(hr); return item;
        }
        internal static string Name(IShellItem item, uint kind) {
            IntPtr p; int hr = item.GetDisplayName(kind, out p); Marshal.ThrowExceptionForHR(hr);
            try { return Marshal.PtrToStringUni(p) ?? ""; } finally { Marshal.FreeCoTaskMem(p); }
        }
        static void Release(object obj) { if (obj != null && Marshal.IsComObject(obj)) Marshal.FinalReleaseComObject(obj); }
        static IFileOperation Operation(IntPtr owner, uint flags) {
            IFileOperation op = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09")));
            try { Marshal.ThrowExceptionForHR(op.SetOwnerWindow(owner)); Marshal.ThrowExceptionForHR(op.SetOperationFlags(flags)); return op; }
            catch { Release(op); throw; }
        }
        public static Receipt Recycle(string raw, IntPtr owner) {
            Receipt result = new Receipt(); result.OriginalPath = raw; IFileOperation op = null; IShellItem item = null;
            try {
                result.OriginalPath = Validate(raw);
                op = Operation(owner, RecycleFlags); item = Item(result.OriginalPath);
                Sink sink = new Sink(result); Marshal.ThrowExceptionForHR(op.DeleteItem(item, sink));
                int hr = op.PerformOperations(); bool aborted; Marshal.ThrowExceptionForHR(op.GetAnyOperationsAborted(out aborted));
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                if (aborted) throw new IOException("回收操作被取消。");
                if (!sink.SawPre || !sink.SawPost || !result.Success) throw new IOException(String.IsNullOrEmpty(result.Error) ? "Windows 没有返回已进入回收站的凭据。" : result.Error);
                if (File.Exists(result.OriginalPath) || String.IsNullOrEmpty(result.RecyclePath) || !File.Exists(result.RecyclePath)) throw new IOException("回收结果未能核实，请打开系统回收站检查。");
            } catch (Exception e) { result.Success = false; result.Error = e.Message; result.HResult = e.HResult; }
            finally { Release(item); Release(op); }
            return result;
        }
        static void RestoreProbe(Receipt receipt, IntPtr owner) {
            if (File.Exists(receipt.OriginalPath)) throw new IOException("测试文件原位置已存在，停止自动恢复。");
            IFileOperation op = null; IShellItem item = null, folder = null;
            try {
                op = Operation(owner, MoveFlags); item = Item(String.IsNullOrEmpty(receipt.ParsingName) ? receipt.RecyclePath : receipt.ParsingName);
                folder = Item(Path.GetDirectoryName(receipt.OriginalPath));
                Marshal.ThrowExceptionForHR(op.MoveItem(item, folder, Path.GetFileName(receipt.OriginalPath), null));
                Marshal.ThrowExceptionForHR(op.PerformOperations()); bool aborted; Marshal.ThrowExceptionForHR(op.GetAnyOperationsAborted(out aborted));
                if (aborted || !File.Exists(receipt.OriginalPath)) throw new IOException("测试文件未自动恢复，可在系统回收站手动还原。");
            } finally { Release(folder); Release(item); Release(op); }
        }
        static string Hash(string path) { using (SHA256 sha = SHA256.Create()) using (FileStream stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)); }
        public static ProbeResult Probe(IntPtr owner) {
            ProbeResult r = new ProbeResult();
            try {
                WindowsSTA();
                string dir = Path.Combine(Path.GetTempPath(), "RecyclePet-Probes", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
                r.OriginalPath = Path.Combine(dir, "recycle-pet-disposable-probe-" + DateTime.Now.ToString("HHmmss") + ".txt");
                File.WriteAllText(r.OriginalPath, "RecyclePet disposable verification file. " + Guid.NewGuid().ToString(), new UTF8Encoding(false));
                string before = Hash(r.OriginalPath); Receipt receipt = Recycle(r.OriginalPath, owner); AppendReceipt(receipt);
                r.Recycled = receipt.Success; r.RecyclePath = receipt.RecyclePath;
                if (!r.Recycled) throw new IOException(receipt.Error);
                RestoreProbe(receipt, owner); r.Restored = File.Exists(r.OriginalPath) && before == Hash(r.OriginalPath);
                if (!r.Restored) throw new IOException("恢复后的内容校验失败。");
                r.Passed = true;
            } catch (Exception e) { r.Error = e.Message; }
            Directory.CreateDirectory(StateRoot); File.WriteAllText(Path.Combine(StateRoot, "last-probe.json"), r.Json(), new UTF8Encoding(false));
            return r;
        }
        public static void AppendReceipt(Receipt receipt) {
            Directory.CreateDirectory(StateRoot); File.AppendAllText(Path.Combine(StateRoot, "receipts-" + DateTime.Now.ToString("yyyy-MM-dd") + ".jsonl"), receipt.Json() + Environment.NewLine, new UTF8Encoding(false));
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, out IShellItem item);
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class Sink : IFileOperationProgressSink {
        readonly Receipt receipt; internal bool SawPre, SawPost;
        public Sink(Receipt receipt) { this.receipt = receipt; }
        public int PreDeleteItem(uint flags, IShellItem item) {
            try {
                string actual = Native.Validate(Native.Name(item, 0x80058000));
                if ((flags & 0x80) == 0 || !actual.Equals(receipt.OriginalPath, StringComparison.OrdinalIgnoreCase)) {
                    receipt.Error = "系统未确认回收模式，已阻止这次删除。"; return unchecked((int)0x80004004);
                }
                SawPre = true; return 0;
            } catch (Exception e) { receipt.Error = e.Message; return unchecked((int)0x80004004); }
        }
        public int PostDeleteItem(uint flags, IShellItem item, int hr, IShellItem created) {
            SawPost = true; receipt.HResult = hr;
            if (hr >= 0 && created != null) {
                try { receipt.RecyclePath = Native.Name(created, 0x80058000); receipt.ParsingName = Native.Name(created, 0x80028000); receipt.Success = !String.IsNullOrEmpty(receipt.RecyclePath); }
                catch (Exception e) { receipt.Error = e.Message; }
            } else receipt.Error = hr < 0 ? "Windows 回收失败，HRESULT=0x" + hr.ToString("X8") : "系统未返回回收站对象，未计为回收成功。";
            return 0;
        }
        public int StartOperations() { return 0; }
        public int FinishOperations(int hr) { return 0; }
        public int PreRenameItem(uint f, IShellItem i, string n) { return 0; }
        public int PostRenameItem(uint f, IShellItem i, string n, int h, IShellItem c) { return 0; }
        public int PreMoveItem(uint f, IShellItem i, IShellItem d, string n) { return 0; }
        public int PostMoveItem(uint f, IShellItem i, IShellItem d, string n, int h, IShellItem c) { return 0; }
        public int PreCopyItem(uint f, IShellItem i, IShellItem d, string n) { return 0; }
        public int PostCopyItem(uint f, IShellItem i, IShellItem d, string n, int h, IShellItem c) { return 0; }
        public int PreNewItem(uint f, IShellItem d, string n) { return 0; }
        public int PostNewItem(uint f, IShellItem d, string n, string t, uint a, int h, IShellItem c) { return 0; }
        public int UpdateProgress(uint total, uint done) { return 0; }
        public int ResetTimer() { return 0; }
        public int PauseTimer() { return 0; }
        public int ResumeTimer() { return 0; }
    }
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem {
        [PreserveSig] int BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr obj);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint kind, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attrs);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }
    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IFileOperation {
        [PreserveSig] int Advise(IFileOperationProgressSink sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint flags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(IntPtr dialog);
        [PreserveSig] int SetProperties(IntPtr properties);
        [PreserveSig] int SetOwnerWindow(IntPtr owner);
        [PreserveSig] int ApplyPropertiesToItem(IShellItem item);
        [PreserveSig] int ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        [PreserveSig] int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
        [PreserveSig] int RenameItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int MoveItem(IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
        [PreserveSig] int MoveItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem folder);
        [PreserveSig] int CopyItem(IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
        [PreserveSig] int CopyItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem folder);
        [PreserveSig] int DeleteItem(IShellItem item, IFileOperationProgressSink sink);
        [PreserveSig] int DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        [PreserveSig] int NewItem(IShellItem folder, uint attrs, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IFileOperationProgressSink sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
    [ComImport, Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IFileOperationProgressSink {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int hr);
        [PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, int hr, IShellItem created);
        [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string name, int hr, IShellItem created);
        [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string name, int hr, IShellItem created);
        [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
        [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int hr, IShellItem created);
        [PreserveSig] int PreNewItem(uint flags, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostNewItem(uint flags, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, uint attrs, int hr, IShellItem created);
        [PreserveSig] int UpdateProgress(uint total, uint done);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }
}
