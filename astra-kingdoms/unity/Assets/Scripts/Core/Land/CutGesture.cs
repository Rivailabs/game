using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;

namespace AstraKingdoms.Client.Land
{
    /// <summary>A gesture-level hint shown before the engine is asked (the engine's rejection reason follows).</summary>
    public enum GestureHint : byte
    {
        None = 0,
        /// <summary>Fewer than three distinct cells: keep drawing.</summary>
        TooShort = 1,
        /// <summary>The finger lifted far from where it started; the loop will be closed with a straight edge.</summary>
        ClosedAutomatically = 2,
        /// <summary>The stroke left the board image; points outside were ignored.</summary>
        LeftTheBoard = 3,
        /// <summary>The stroke had more points than the 128-vertex limit and was simplified.</summary>
        Simplified = 4,
    }

    /// <summary>
    /// Finger-cut capture and quantization (ticket 39). Pointer samples in normalized board-image
    /// coordinates are snapped to cell centres with the rules' own helper, de-duplicated, thinned
    /// by a minimum spacing, and simplified to at most 128 vertices. The result is only ever a
    /// <i>proposal</i>: legality, quota and the transferred cells come from the engine's preview,
    /// so a bad gesture can produce a rejection reason but never an over-quota transfer or a change
    /// to logical ownership.
    /// </summary>
    public sealed class CutGesture
    {
        /// <summary>Samples closer than this (in cells) to the previous kept point are ignored.</summary>
        public const int MinSpacingCells = 1;
        /// <summary>End within this many cells of the start counts as a deliberately closed loop.</summary>
        public const int CloseDistanceCells = 12;

        private readonly List<CellPoint> _raw = new List<CellPoint>();
        private bool _leftBoard;

        public bool Drawing { get; private set; }
        public IReadOnlyList<CellPoint> RawCells => _raw;

        public void Begin(double u, double vDown)
        {
            _raw.Clear();
            _leftBoard = false;
            Drawing = true;
            Add(u, vDown);
        }

        /// <summary>Adds a pointer sample; returns true when the stroke changed.</summary>
        public bool Add(double u, double vDown)
        {
            if (!Drawing) return false;
            if (double.IsNaN(u) || double.IsNaN(vDown) || u < 0 || u > 1 || vDown < 0 || vDown > 1)
            {
                _leftBoard = true;
                return false;
            }
            CellPoint c = BoardMapping.Snap(u, vDown);
            if (_raw.Count > 0)
            {
                CellPoint last = _raw[_raw.Count - 1];
                if (Math.Max(Math.Abs(last.X - c.X), Math.Abs(last.Y - c.Y)) < MinSpacingCells) return false;
            }
            _raw.Add(c);
            return true;
        }

        public void End(double u, double vDown)
        {
            Add(u, vDown);
            Drawing = false;
        }

        public void Clear()
        {
            _raw.Clear();
            _leftBoard = false;
            Drawing = false;
        }

        /// <summary>The polygon to submit (at most 128 vertices) and a gesture hint.</summary>
        public List<CellPoint> Polygon(out GestureHint hint)
        {
            List<CellPoint> deduped = StrokeSimplifier.Dedupe(_raw);
            hint = GestureHint.None;
            if (deduped.Count < 3)
            {
                hint = GestureHint.TooShort;
                return deduped;
            }
            List<CellPoint> poly = StrokeSimplifier.Simplify(deduped);
            if (deduped.Count > RulesConstants.MaxCutVertices) hint = GestureHint.Simplified;
            CellPoint a = deduped[0], b = deduped[deduped.Count - 1];
            if (Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y)) > CloseDistanceCells) hint = GestureHint.ClosedAutomatically;
            if (_leftBoard) hint = GestureHint.LeftTheBoard;
            return poly;
        }

        public static string HintKey(GestureHint hint) => hint == GestureHint.None ? null : "land.gesture." + hint;
    }
}
