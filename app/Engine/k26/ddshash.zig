// ddshash <file> <offset> <length>  -> XXH3-64 (seed 0) of that byte range, as 16 hex digits
const std = @import("std");
pub fn main() !void {
    var gpa = std.heap.GeneralPurposeAllocator(.{}){};
    const a = gpa.allocator();
    const args = try std.process.argsAlloc(a);
    const f = try std.fs.cwd().openFile(args[1], .{});
    const off = try std.fmt.parseInt(u64, args[2], 10);
    const len = try std.fmt.parseInt(usize, args[3], 10);
    const buf = try a.alloc(u8, len);
    try f.seekTo(off);
    _ = try f.readAll(buf);
    const h = std.hash.XxHash3.hash(0, buf);
    try std.io.getStdOut().writer().print("{x:0>16}\n", .{h});
}
