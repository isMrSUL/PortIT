// PortIT 2026-11-09: Yuke's BPE ("BPE " entries, Philip Gage byte pairs) - the same output, byte for byte, as
// svrfmt.bpe_encode(raw, compress=True) in Python, which took ~90% of a port's ch.pac step (40-70 s a wrestler).
//   bpe.exe <raw in> <bpe out>        (encode)
//   bpe.exe -d <bpe in> <raw out>     (decode one "BPE " entry - svrfmt.bpe_decode's work, native)
// Per 4000-byte block: while a free byte code is left, count adjacent pairs, take the most frequent (a tie: the pair
// seen first), stop under 4, replace it left to right with the lowest free code; then the pair table + u16 length
// + data. Build: zig build-exe bpe.zig -O ReleaseFast
const std = @import("std");

const STEP: usize = 4000;

fn table(out: *std.ArrayList(u8), left: *const [256]u8, right: *const [256]u8) !void {
    var c: usize = 0;
    while (c < 256) {
        var skip: usize = 0;
        while (c + skip < 256 and left[c + skip] == c + skip and skip < 128) skip += 1;
        if (skip > 0) {
            try out.append(@intCast(127 + skip));
            c += skip;
            if (c == 256) break;
            try out.append(left[c]);
            if (left[c] != c) try out.append(right[c]);
            c += 1;
            continue;
        }
        var run: usize = 0;
        while (c + run < 256 and left[c + run] != c + run and run < 128) run += 1;
        try out.append(@intCast(run - 1));
        var k = c;
        while (k < c + run) : (k += 1) {
            try out.append(left[k]);
            try out.append(right[k]);
        }
        c += run;
    }
}

fn block(out: *std.ArrayList(u8), src: []const u8, counts: []u32, first: []u32, seen: []u16) !void {
    var buf: [STEP]u8 = undefined;
    var tmp: [STEP]u8 = undefined;
    var n: usize = src.len;
    @memcpy(buf[0..n], src);
    var left: [256]u8 = undefined;
    var right: [256]u8 = undefined;
    var used = [_]bool{false} ** 256;
    for (0..256) |i| {
        left[i] = @intCast(i);
        right[i] = 0;
    }
    for (buf[0..n]) |b| used[b] = true;
    var free: [256]u8 = undefined;
    var nfree: usize = 0;
    var fpos: usize = 0;
    for (0..256) |i| {
        if (!used[i]) {
            free[nfree] = @intCast(i);
            nfree += 1;
        }
    }
    while (fpos < nfree) {
        if (n < 2) break;
        var nseen: usize = 0;
        var i: usize = 0;
        while (i + 1 < n) : (i += 1) {
            const p: u16 = @as(u16, buf[i]) | (@as(u16, buf[i + 1]) << 8);
            if (counts[p] == 0) {
                first[p] = @intCast(i);
                seen[nseen] = p;
                nseen += 1;
            }
            counts[p] += 1;
        }
        // the most frequent pair; a tie goes to the one seen first (Python's max over a dict in insertion order)
        var best: u32 = 0;
        var pair: u16 = 0;
        var bfirst: u32 = std.math.maxInt(u32);
        for (seen[0..nseen]) |p| {
            const cnt = counts[p];
            if (cnt > best or (cnt == best and first[p] < bfirst)) {
                best = cnt;
                pair = p;
                bfirst = first[p];
            }
        }
        for (seen[0..nseen]) |p| counts[p] = 0;
        if (best < 4) break;
        const code = free[fpos];
        fpos += 1;
        const a: u8 = @intCast(pair & 0xFF);
        const b: u8 = @intCast(pair >> 8);
        var m: usize = 0;
        i = 0;
        while (i < n) {
            if (i + 1 < n and buf[i] == a and buf[i + 1] == b) {
                tmp[m] = code;
                i += 2;
            } else {
                tmp[m] = buf[i];
                i += 1;
            }
            m += 1;
        }
        @memcpy(buf[0..m], tmp[0..m]);
        n = m;
        left[code] = a;
        right[code] = b;
    }
    try table(out, &left, &right);
    try out.append(@intCast(n & 0xFF));
    try out.append(@intCast(n >> 8));
    try out.appendSlice(buf[0..n]);
}

fn expand(out: *std.ArrayList(u8), s: u8, left: *const [256]u8, right: *const [256]u8) !void {
    var stack: [512]u8 = undefined;
    var top: usize = 1;
    stack[0] = s;
    while (top > 0) {
        top -= 1;
        const c = stack[top];
        if (left[c] == c) {
            try out.append(c);
        } else {
            stack[top] = right[c];
            stack[top + 1] = left[c];
            top += 2;
        }
    }
}

fn decode(al: std.mem.Allocator, src: []const u8) !std.ArrayList(u8) {
    if (src.len < 16 or !std.mem.eql(u8, src[0..4], "BPE ")) return error.NotBpe;
    const packed_len = std.mem.readInt(u32, src[8..12], .little);
    const unpacked = std.mem.readInt(u32, src[12..16], .little);
    var out = std.ArrayList(u8).init(al);
    try out.ensureTotalCapacity(unpacked);
    var pos: usize = 16;
    const end: usize = 16 + packed_len;
    while (pos < end) {
        var left: [256]u8 = undefined;
        var right = [_]u8{0} ** 256;
        for (0..256) |i| left[i] = @intCast(i);
        var c: usize = 0;
        var count: usize = src[pos];
        pos += 1;
        while (true) {
            if (count > 127) {
                c += count - 127;
                count = 0;
            }
            if (c == 256) break;
            var k: usize = 0;
            while (k < count + 1) : (k += 1) {
                left[c] = src[pos];
                pos += 1;
                if (c != left[c]) {
                    right[c] = src[pos];
                    pos += 1;
                }
                c += 1;
            }
            if (c == 256) break;
            count = src[pos];
            pos += 1;
        }
        const size: usize = @as(usize, src[pos]) | (@as(usize, src[pos + 1]) << 8);
        pos += 2;
        for (src[pos .. pos + size]) |b| try expand(&out, b, &left, &right);
        pos += size;
    }
    if (out.items.len != unpacked) return error.BadSize;
    return out;
}

pub fn main() !void {
    var gpa = std.heap.GeneralPurposeAllocator(.{}){};
    defer _ = gpa.deinit();
    const al = gpa.allocator();
    const args = try std.process.argsAlloc(al);
    defer std.process.argsFree(al, args);
    if (args.len == 4 and std.mem.eql(u8, args[1], "-d")) {
        const src = try std.fs.cwd().readFileAlloc(al, args[2], 1 << 30);
        defer al.free(src);
        var out = try decode(al, src);
        defer out.deinit();
        const f = try std.fs.cwd().createFile(args[3], .{});
        defer f.close();
        try f.writeAll(out.items);
        return;
    }
    if (args.len != 3) {
        std.debug.print("usage: bpe <raw in> <bpe out>\n", .{});
        std.process.exit(2);
    }
    const raw = try std.fs.cwd().readFileAlloc(al, args[1], 1 << 30);
    defer al.free(raw);
    const counts = try al.alloc(u32, 65536);
    defer al.free(counts);
    @memset(counts, 0);
    const first = try al.alloc(u32, 65536);
    defer al.free(first);
    const seen = try al.alloc(u16, 65536);
    defer al.free(seen);
    var body = std.ArrayList(u8).init(al);
    defer body.deinit();
    var s: usize = 0;
    while (s < raw.len) : (s += STEP) {
        const e = @min(s + STEP, raw.len);
        try block(&body, raw[s..e], counts, first, seen);
    }
    var hdr: [16]u8 = undefined;
    @memcpy(hdr[0..4], "BPE ");
    std.mem.writeInt(u32, hdr[4..8], 0x100, .little);
    std.mem.writeInt(u32, hdr[8..12], @intCast(body.items.len), .little);
    std.mem.writeInt(u32, hdr[12..16], @intCast(raw.len), .little);
    const f = try std.fs.cwd().createFile(args[2], .{});
    defer f.close();
    try f.writeAll(&hdr);
    try f.writeAll(body.items);
}
