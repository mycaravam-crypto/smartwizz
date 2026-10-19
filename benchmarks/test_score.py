"""Offline tests for benchmark scoring and semantic adjudication."""
import tempfile
import unittest
from pathlib import Path

from score import locations, score


class ScoreTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.fixture = Path(self.temp.name) / "RuleViolations.cs"
        self.fixture.write_text("first issue\nsecond issue\n", encoding="utf-8")
        self.key = {
            "fixture": "benchmarks/fixtures/RuleViolations.cs",
            "defects": [
                {"id": "B01", "anchor": "first issue"},
                {"id": "B02", "anchor": "second issue"},
            ],
        }
        self.expected = locations(self.key, self.fixture)

    def finding(self, fp, line):
        return {"fingerprint": fp, "filePath": self.key["fixture"],
                "startLine": line, "ruleId": "standard-example"}

    def test_distinct_location_candidates_are_counted_once(self):
        report = {"findings": [self.finding("a", 1), self.finding("b", 1),
                               self.finding("c", 99)], "models": ["test"]}
        result = score(self.key, report, self.expected)
        self.assertEqual(1, result["matched"])
        self.assertEqual(2, result["falsePositiveCandidates"])
        self.assertEqual(1, result["falseNegativeCandidates"])
        self.assertEqual(0.5, result["recallCandidate"])
        self.assertEqual(["B01"], result["matchedIds"])
        self.assertNotIn("verified", result)

    def test_manual_adjudication_is_complete_and_location_checked(self):
        report = {"findings": [self.finding("a", 1), self.finding("b", 99)]}
        verified = score(self.key, report, self.expected, {"a": "B01", "b": None})
        self.assertEqual(0.5, verified["verified"]["precision"])
        self.assertEqual(0.5, verified["verified"]["recall"])
        self.assertEqual(1, verified["verified"]["falsePositive"])

        with self.assertRaises(ValueError):
            score(self.key, report, self.expected, {"a": "B01"})
        with self.assertRaises(ValueError):
            score(self.key, report, self.expected, {"a": "B02", "b": None})

    def test_missing_or_duplicate_ground_truth_anchor_is_rejected(self):
        self.fixture.write_text("first issue\nfirst issue\n", encoding="utf-8")
        with self.assertRaises(ValueError):
            locations(self.key, self.fixture)


if __name__ == "__main__":
    unittest.main()
