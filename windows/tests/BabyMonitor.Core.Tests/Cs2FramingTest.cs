using System.Text;
using BabyMonitor.Core.Xiaomi;
using Xunit;

namespace BabyMonitor.Core.Tests;

public class Cs2FramingTest
{
    [Fact(DisplayName = "PROTO-17 command frames carry BE sizes, channel, seq and the record prefix")]
    public void CommandFrames()
    {
        var payload = new byte[] { 1, 2, 3 };
        var frame = Cs2.MarshalCmd(channel: 0, seq: 0x0102, cmd: 0x100, payload: payload);
        Assert.Equal(0xf1, frame[0]);
        Assert.Equal(0xd0, frame[1]);
        Assert.Equal(12 + payload.Length, frame.BeU16(2)); // body size
        Assert.Equal(0xd1, frame[4]);
        Assert.Equal(0, frame[5]);
        Assert.Equal(0x0102, frame.BeU16(6));
        Assert.Equal(4 + payload.Length, frame.BeU32(8)); // record length prefix
        Assert.Equal(0x100L, frame.BeU32(12));
        Assert.Equal(payload, frame.Slice(16, frame.Length));
    }

    [Fact(DisplayName = "PROTO-16 tcp frames have BE u16 size and 0x68 magic in an 8-byte header")]
    public void TcpFrames()
    {
        var body = new byte[] { 9, 8, 7, 6, 5 };
        var framed = Cs2.TcpFrame(body);
        Assert.Equal(8 + body.Length, framed.Length);
        Assert.Equal(body.Length, framed.BeU16(0));
        Assert.Equal(0x68, framed[2]);
        Assert.Equal(body, framed.Slice(8, framed.Length));
    }

    [Fact(DisplayName = "PROTO-17 udp acks echo channel and sequence")]
    public void UdpAcks()
    {
        var ack = Cs2.UdpAck(2, 0x01, 0x02);
        Assert.Equal(new byte[] { 0xf1, 0xd1, 0, 6, 0xd1, 2, 0, 1, 0x01, 0x02 }, ack);
    }

    [Fact(DisplayName = "PROTO-18 records reassemble regardless of chunk boundaries")]
    public void RecordsReassemble()
    {
        var asm = new RecordAssembler();
        var record = Encoding.UTF8.GetBytes("hello miss");
        var stream = new byte[4 + record.Length];
        stream.PutBeU32(0, record.Length);
        Buffer.BlockCopy(record, 0, stream, 4, record.Length);

        // Split awkwardly: 3 bytes (inside the length prefix), then the rest.
        Assert.Empty(asm.Push(stream.Slice(0, 3)));
        var out_ = asm.Push(stream.Slice(3, stream.Length));
        Assert.Single(out_);
        Assert.Equal(record, out_[0]);
    }

    [Fact(DisplayName = "PROTO-18 a record completed by a chunk of a few bytes is delivered immediately")]
    public void ShortRecordsComeOutAtOnce()
    {
        // A short command record (an ack) whose last bytes arrive alone must not sit in the assembler
        // until unrelated traffic happens to flush it out.
        var asm = new RecordAssembler();
        var record = new byte[] { 7, 8, 9 };
        var stream = new byte[4 + record.Length];
        stream.PutBeU32(0, record.Length);
        Buffer.BlockCopy(record, 0, stream, 4, record.Length);

        Assert.Empty(asm.Push(stream.Slice(0, 5))); // prefix + first byte
        var out_ = asm.Push(stream.Slice(5, stream.Length)); // the final two bytes
        Assert.Single(out_);
        Assert.Equal(record, out_[0]);
    }

    [Fact(DisplayName = "PROTO-18 a corrupt record length is a dead connection — not a crash")]
    public void CorruptLengthIsADeadConnection()
    {
        // A length prefix near 2^31 would try to allocate gigabytes and take the process with it. It
        // must surface as a protocol error instead.
        var asm = new RecordAssembler();
        var corrupt = new byte[8];
        corrupt.PutBeU32(0, 0xFFFFFFFFL);
        var err = Assert.Throws<XiaomiException>(() => asm.Push(corrupt));
        Assert.Contains("corrupt record length", err.Message, StringComparison.Ordinal);

        // A merely-huge (positive) length is equally impossible for a real record.
        var huge = new byte[8];
        huge.PutBeU32(0, 0x40000000L); // 1 GiB
        Assert.Throws<XiaomiException>(() => new RecordAssembler().Push(huge));
    }

    [Fact(DisplayName = "PROTO-18 multiple records in one chunk all come out")]
    public void MultipleRecordsInOneChunk()
    {
        var asm = new RecordAssembler();
        var recA = new byte[] { 1, 1, 1, 1, 1 };
        var recB = new byte[] { 2, 2 };
        var stream = new byte[4 + recA.Length + 4 + recB.Length];
        stream.PutBeU32(0, recA.Length);
        Buffer.BlockCopy(recA, 0, stream, 4, recA.Length);
        stream.PutBeU32(4 + recA.Length, recB.Length);
        Buffer.BlockCopy(recB, 0, stream, 8 + recA.Length, recB.Length);

        var out_ = asm.Push(stream);
        Assert.Equal(2, out_.Count);
        Assert.Equal(recA, out_[0]);
        Assert.Equal(recB, out_[1]);
    }
}
