using System.Security.Cryptography;
using System.Text;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;

namespace LongRunningMcp;

/// <summary>
/// A fun, dependency-free long-running orchestration: a tiny proof-of-work "miner".
///
/// It mines a short chain of blocks. Each block must have a SHA-256 hash with at least
/// <c>difficulty</c> leading zero bits, found by trying nonces 0, 1, 2, ... until one works. Each
/// block also includes the previous block's hash, so the blocks form a chain -- which makes this a
/// natural example of Durable's *function-chaining* pattern: every step depends on the output of the
/// step before it.
///
/// The work is real CPU work (no Task.Delay, no external services), and its duration is controlled
/// entirely by <c>difficulty</c>: each extra bit roughly doubles the expected number of hashes, so
/// higher difficulty = longer mining. That single knob is what lets the sample demonstrate both the
/// inline path (quick) and the poll path (slow) -- see MiningTools and the README.
/// </summary>
public static class MiningOrchestrator
{
    // The chain length is fixed; difficulty is the knob that controls how long mining takes.
    private const int BlockCount = 4;

    [Function(nameof(RunOrchestrator))]
    public static async Task<string> RunOrchestrator(
        [OrchestrationTrigger] TaskOrchestrationContext context,
        int difficulty)
    {
        ILogger logger = context.CreateReplaySafeLogger(nameof(MiningOrchestrator));
        logger.LogInformation("Mining {Count} blocks at difficulty {Difficulty}", BlockCount, difficulty);

        // Chain the blocks: each block is mined from the previous block's hash, so step N+1 depends
        // on step N's output. The CPU-heavy mining happens in the activity, keeping the orchestrator
        // deterministic (a requirement for Durable orchestrations).
        var blocks = new List<MinedBlock>();
        string previousHash = "GENESIS";
        for (int index = 1; index <= BlockCount; index++)
        {
            MinedBlock block = await context.CallActivityAsync<MinedBlock>(
                nameof(MineBlock), new MineBlockInput(index, previousHash, difficulty));
            blocks.Add(block);
            previousHash = block.Hash;
        }

        var report = new StringBuilder();
        report.AppendLine($"# Mined {blocks.Count} blocks at difficulty {difficulty}");
        report.AppendLine();
        foreach (MinedBlock b in blocks)
        {
            report.AppendLine(
                $"- Block {b.Index}: nonce={b.Nonce}, attempts={b.Attempts:N0}, hash={b.Hash[..16].ToLowerInvariant()}…");
        }

        return report.ToString();
    }

    /// <summary>
    /// Mines a single block: scans nonces from 0 upward until the SHA-256 hash of
    /// "{index}:{previousHash}:{nonce}" has at least <c>difficulty</c> leading zero bits.
    /// Deterministic for a given input (so it is safe to replay), but how long it takes grows
    /// exponentially with difficulty.
    /// </summary>
    [Function(nameof(MineBlock))]
    public static MinedBlock MineBlock([ActivityTrigger] MineBlockInput input)
    {
        long nonce = 0;
        while (true)
        {
            byte[] hash = SHA256.HashData(
                Encoding.UTF8.GetBytes($"{input.Index}:{input.PreviousHash}:{nonce}"));

            if (LeadingZeroBits(hash) >= input.Difficulty)
            {
                return new MinedBlock(input.Index, nonce, Convert.ToHexString(hash), nonce + 1);
            }

            nonce++;
        }
    }

    private static int LeadingZeroBits(byte[] hash)
    {
        int bits = 0;
        foreach (byte b in hash)
        {
            if (b == 0)
            {
                bits += 8;
                continue;
            }

            for (int mask = 0x80; mask > 0; mask >>= 1)
            {
                if ((b & mask) == 0) bits++;
                else return bits;
            }
        }

        return bits;
    }
}

public record MineBlockInput(int Index, string PreviousHash, int Difficulty);

public record MinedBlock(int Index, long Nonce, string Hash, long Attempts);
