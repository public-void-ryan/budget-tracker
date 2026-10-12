using BudgetTracker.Api.Imports;

namespace BudgetTracker.Api.Tests.Imports;

public class BoundedBodyTests
{
    private const int Limit = 1_000;

    /// <summary>A stream that cannot report its length or seek, like a chunked upload.</summary>
    private sealed class Chunked(byte[] bytes, int chunkSize = 300) : Stream
    {
        private int _position;

        public int BytesRead => _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(Math.Min(count, chunkSize), bytes.Length - _position);
            Array.Copy(bytes, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static byte[] Bytes(int count) => Enumerable.Repeat((byte)'x', count).ToArray();

    [Fact]
    public async Task A_small_body_is_returned_whole()
    {
        var result = await BoundedBody.ReadAsync(new Chunked(Bytes(10)), declaredLength: null, Limit, CancellationToken.None);

        Assert.Equal(Bytes(10), result);
    }

    [Fact]
    public async Task A_body_of_exactly_the_limit_is_accepted_across_several_reads()
    {
        var result = await BoundedBody.ReadAsync(new Chunked(Bytes(Limit)), declaredLength: null, Limit, CancellationToken.None);

        Assert.Equal(Limit, result!.Length);
    }

    [Fact]
    public async Task One_byte_over_the_limit_with_no_declared_length_is_refused_while_reading()
    {
        // The guard that matters for a chunked upload: nothing declares the size, so only the
        // running total can catch it.
        var result = await BoundedBody.ReadAsync(new Chunked(Bytes(Limit + 1)), declaredLength: null, Limit, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Reading_stops_soon_after_the_limit_instead_of_consuming_the_whole_body()
    {
        var stream = new Chunked(Bytes(1_000_000));

        var result = await BoundedBody.ReadAsync(stream, declaredLength: null, Limit, CancellationToken.None);

        Assert.Null(result);
        Assert.True(stream.BytesRead <= Limit + 300, $"read {stream.BytesRead} bytes");
    }

    [Fact]
    public async Task A_declared_length_over_the_limit_is_refused_without_reading_anything()
    {
        var stream = new Chunked(Bytes(10));

        var result = await BoundedBody.ReadAsync(stream, declaredLength: Limit + 1, Limit, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, stream.BytesRead);
    }

    [Fact]
    public async Task A_declared_length_that_understates_the_body_is_not_trusted()
    {
        var result = await BoundedBody.ReadAsync(new Chunked(Bytes(Limit + 500)), declaredLength: 10, Limit, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task An_empty_body_is_an_empty_array_not_null()
    {
        var result = await BoundedBody.ReadAsync(new Chunked([]), declaredLength: 0, Limit, CancellationToken.None);

        Assert.Empty(result!);
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => BoundedBody.ReadAsync(new MemoryStream(Bytes(10)), declaredLength: null, Limit, source.Token));
    }
}
