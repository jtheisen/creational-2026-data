namespace Creational.Dataflow.Sql;

public readonly record struct TaxonEdge(int OttId, int? ParentOttId);
public readonly record struct TaxonInterval(int OttId, int? ParentOttId, int Start, int EndExclusive);

public static class TaxonDfs
{
    private readonly record struct Frame(int Index, int NextChild);

    public static TaxonInterval[] Build(IReadOnlyList<TaxonEdge> edges)
    {
        var sorted = edges.ToArray();
        Array.Sort(sorted, static (left, right) => left.OttId.CompareTo(right.OttId));
        var positions = new Dictionary<int, int>(sorted.Length);
        for (var i = 0; i < sorted.Length; i++)
        {
            if (sorted[i].OttId <= 0 || i > 0 && sorted[i].OttId == sorted[i - 1].OttId)
                throw new InvalidDataException("OTT IDs must be positive and unique.");
            positions.Add(sorted[i].OttId, i);
        }

        var firstChild = new int[sorted.Length];
        var nextSibling = new int[sorted.Length];
        Array.Fill(firstChild, -1);
        Array.Fill(nextSibling, -1);
        var roots = new List<int>();
        for (var i = sorted.Length - 1; i >= 0; i--)
        {
            if (sorted[i].ParentOttId is not { } parent)
            {
                roots.Add(i);
                continue;
            }
            if (!positions.TryGetValue(parent, out var parentIndex) || parent == sorted[i].OttId)
                throw new InvalidDataException($"Taxon {sorted[i].OttId} has an invalid parent {parent}.");
            nextSibling[i] = firstChild[parentIndex];
            firstChild[parentIndex] = i;
        }

        var starts = new int[sorted.Length];
        var ends = new int[sorted.Length];
        var seen = new bool[sorted.Length];
        var stack = new List<Frame>();
        var order = 0;
        for (var root = roots.Count - 1; root >= 0; root--)
        {
            Visit(roots[root]);
            while (stack.Count > 0)
            {
                var top = stack.Count - 1;
                var frame = stack[top];
                if (frame.NextChild >= 0)
                {
                    stack[top] = frame with { NextChild = nextSibling[frame.NextChild] };
                    Visit(frame.NextChild);
                }
                else
                {
                    ends[frame.Index] = order;
                    stack.RemoveAt(top);
                }
            }
        }
        if (order != sorted.Length)
            throw new InvalidDataException("OTT parent links contain a cycle or have no root.");

        var result = new TaxonInterval[sorted.Length];
        for (var i = 0; i < result.Length; i++)
            result[i] = new(sorted[i].OttId, sorted[i].ParentOttId, starts[i], ends[i]);
        return result;

        void Visit(int index)
        {
            if (seen[index]) throw new InvalidDataException("OTT parent links contain a cycle.");
            seen[index] = true;
            starts[index] = order++;
            stack.Add(new Frame(index, firstChild[index]));
        }
    }
}
