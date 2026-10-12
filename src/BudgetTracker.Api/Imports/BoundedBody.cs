namespace BudgetTracker.Api.Imports;

public static class BoundedBody
{
    /// <summary>
    /// Reads a request body without ever holding more than <paramref name="maxBytes"/>: returns
    /// null as soon as it is clear the body is larger. A declared length over the limit is refused
    /// without reading anything, but the declared length is not trusted: it can be absent (a
    /// chunked upload) or untrue, so the limit is also enforced while reading.
    /// </summary>
    public static async Task<byte[]?> ReadAsync(
        Stream body, long? declaredLength, int maxBytes, CancellationToken ct)
    {
        if (declaredLength > maxBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
