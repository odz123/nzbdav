using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Benchmark to compare old vs new RAR3 key derivation performance.
///
/// To run this benchmark:
/// 1. Copy this file to a new console project
/// 2. Run: dotnet run -c Release
///
/// Expected improvements:
/// - Memory usage: ~8MB -> ~1KB (99.99% reduction)
/// - Speed: Should be faster due to better cache locality
/// </summary>
public class Rar3KeyDerivationBenchmark
{
    public static void Main(string[] args)
    {
        Console.WriteLine("RAR3 Key Derivation Benchmark");
        Console.WriteLine("==============================\n");

        var password = "TestPassword123";
        var salt = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };
        var iterations = 5; // Number of iterations for benchmark

        Console.WriteLine($"Testing with password: {password}");
        Console.WriteLine($"Iterations: {iterations}\n");

        // Warm up
        var oldResult = OldImplementation(password, salt);
        var newResult = NewImplementation(password, salt);

        // Verify correctness
        Console.WriteLine("Verifying correctness...");
        if (CompareResults(oldResult, newResult))
        {
            Console.WriteLine("✓ Results match! Optimization is correct.\n");
        }
        else
        {
            Console.WriteLine("✗ ERROR: Results don't match! There's a bug in the optimization.\n");
            PrintResults("Old", oldResult);
            PrintResults("New", newResult);
            return;
        }

        // Benchmark old implementation
        Console.WriteLine("Benchmarking OLD implementation...");
        var oldMemoryBefore = GC.GetTotalMemory(true);
        var oldWatch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            OldImplementation(password, salt);
        }
        oldWatch.Stop();
        var oldMemoryAfter = GC.GetTotalMemory(false);
        var oldMemoryUsed = oldMemoryAfter - oldMemoryBefore;

        // Benchmark new implementation
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Console.WriteLine("Benchmarking NEW implementation...");
        var newMemoryBefore = GC.GetTotalMemory(true);
        var newWatch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            NewImplementation(password, salt);
        }
        newWatch.Stop();
        var newMemoryAfter = GC.GetTotalMemory(false);
        var newMemoryUsed = newMemoryAfter - newMemoryBefore;

        // Print results
        Console.WriteLine("\nResults:");
        Console.WriteLine("--------");
        Console.WriteLine($"Old Implementation:");
        Console.WriteLine($"  Time: {oldWatch.ElapsedMilliseconds}ms ({oldWatch.ElapsedMilliseconds / (double)iterations:F2}ms avg)");
        Console.WriteLine($"  Memory delta: {oldMemoryUsed:N0} bytes");

        Console.WriteLine($"\nNew Implementation:");
        Console.WriteLine($"  Time: {newWatch.ElapsedMilliseconds}ms ({newWatch.ElapsedMilliseconds / (double)iterations:F2}ms avg)");
        Console.WriteLine($"  Memory delta: {newMemoryUsed:N0} bytes");

        Console.WriteLine($"\nImprovement:");
        var speedup = oldWatch.ElapsedMilliseconds / (double)newWatch.ElapsedMilliseconds;
        Console.WriteLine($"  Speed: {speedup:F2}x faster");
        if (oldMemoryUsed > 0)
        {
            var memoryReduction = (1 - newMemoryUsed / (double)oldMemoryUsed) * 100;
            Console.WriteLine($"  Memory: {memoryReduction:F1}% reduction");
        }
    }

    private static (byte[] iv, byte[] key) OldImplementation(string password, byte[] salt)
    {
        const int sizeInitV = 0x10;
        const int sizeSalt30 = 0x08;
        var aesIV = new byte[sizeInitV];

        var rawLength = 2 * password.Length;
        var rawPassword = new byte[rawLength + sizeSalt30];
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        for (var i = 0; i < password.Length; i++)
        {
            rawPassword[i * 2] = passwordBytes[i];
            rawPassword[(i * 2) + 1] = 0;
        }

        for (var i = 0; i < salt.Length; i++)
        {
            rawPassword[i + rawLength] = salt[i];
        }

        var msgDigest = SHA1.Create();
        const int noOfRounds = (1 << 18);
        const int iblock = 3;

        byte[] digest;
        var data = new byte[(rawPassword.Length + iblock) * noOfRounds];

        for (var i = 0; i < noOfRounds; i++)
        {
            rawPassword.CopyTo(data, i * (rawPassword.Length + iblock));

            data[(i * (rawPassword.Length + iblock)) + rawPassword.Length + 0] = (byte)i;
            data[(i * (rawPassword.Length + iblock)) + rawPassword.Length + 1] = (byte)(i >> 8);
            data[(i * (rawPassword.Length + iblock)) + rawPassword.Length + 2] = (byte)(i >> 16);

            if (i % (noOfRounds / sizeInitV) == 0)
            {
                digest = msgDigest.ComputeHash(data, 0, (i + 1) * (rawPassword.Length + iblock));
                aesIV[i / (noOfRounds / sizeInitV)] = digest[19];
            }
        }

        digest = msgDigest.ComputeHash(data);

        var aesKey = new byte[sizeInitV];
        for (var i = 0; i < 4; i++)
        {
            for (var j = 0; j < 4; j++)
            {
                aesKey[(i * 4) + j] = (byte)(
                    (
                        ((digest[i * 4] * 0x1000000) & 0xff000000)
                        | (uint)((digest[(i * 4) + 1] * 0x10000) & 0xff0000)
                        | (uint)((digest[(i * 4) + 2] * 0x100) & 0xff00)
                        | (uint)(digest[(i * 4) + 3] & 0xff)
                    ) >> (j * 8)
                );
            }
        }

        return (aesIV, aesKey);
    }

    private static (byte[] iv, byte[] key) NewImplementation(string password, byte[] salt)
    {
        const int sizeInitV = 0x10;
        const int sizeSalt30 = 0x08;
        var aesIV = new byte[sizeInitV];

        var rawLength = 2 * password.Length;
        var rawPassword = new byte[rawLength + sizeSalt30];
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        for (var i = 0; i < password.Length; i++)
        {
            rawPassword[i * 2] = passwordBytes[i];
            rawPassword[(i * 2) + 1] = 0;
        }

        for (var i = 0; i < salt.Length; i++)
        {
            rawPassword[i + rawLength] = salt[i];
        }

        const int noOfRounds = (1 << 18);
        const int iblock = 3;

        var ivCheckpointInterval = noOfRounds / sizeInitV;

        using var finalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var ivHashes = new IncrementalHash[sizeInitV];
        for (var i = 0; i < sizeInitV; i++)
        {
            ivHashes[i] = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        }

        try
        {
            Span<byte> block = stackalloc byte[rawPassword.Length + iblock];

            for (var i = 0; i < noOfRounds; i++)
            {
                rawPassword.AsSpan().CopyTo(block);
                block[rawPassword.Length + 0] = (byte)i;
                block[rawPassword.Length + 1] = (byte)(i >> 8);
                block[rawPassword.Length + 2] = (byte)(i >> 16);

                finalHash.AppendData(block);

                for (var ivIdx = 0; ivIdx < sizeInitV; ivIdx++)
                {
                    var checkpointIteration = ivIdx * ivCheckpointInterval;
                    if (i <= checkpointIteration)
                    {
                        ivHashes[ivIdx].AppendData(block);

                        if (i == checkpointIteration)
                        {
                            var ivDigest = ivHashes[ivIdx].GetHashAndReset();
                            aesIV[ivIdx] = ivDigest[19];
                        }
                    }
                }
            }

            var digest = finalHash.GetHashAndReset();

            var aesKey = new byte[sizeInitV];
            for (var i = 0; i < 4; i++)
            {
                for (var j = 0; j < 4; j++)
                {
                    aesKey[(i * 4) + j] = (byte)(
                        (
                            ((digest[i * 4] * 0x1000000) & 0xff000000)
                            | (uint)((digest[(i * 4) + 1] * 0x10000) & 0xff0000)
                            | (uint)((digest[(i * 4) + 2] * 0x100) & 0xff00)
                            | (uint)(digest[(i * 4) + 3] & 0xff)
                        ) >> (j * 8)
                    );
                }
            }

            return (aesIV, aesKey);
        }
        finally
        {
            foreach (var hash in ivHashes)
            {
                hash?.Dispose();
            }
        }
    }

    private static bool CompareResults((byte[] iv, byte[] key) result1, (byte[] iv, byte[] key) result2)
    {
        return result1.iv.AsSpan().SequenceEqual(result2.iv) &&
               result1.key.AsSpan().SequenceEqual(result2.key);
    }

    private static void PrintResults(string label, (byte[] iv, byte[] key) result)
    {
        Console.WriteLine($"{label}:");
        Console.WriteLine($"  IV:  {BitConverter.ToString(result.iv).Replace("-", "")}");
        Console.WriteLine($"  Key: {BitConverter.ToString(result.key).Replace("-", "")}");
    }
}
