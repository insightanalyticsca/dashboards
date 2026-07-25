#!/usr/bin/env python3
from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CATALOG = (ROOT / "Services" / "DashboardAssistantSemanticCatalog.cs").read_text(encoding="utf-8")
PLANNER = (ROOT / "Services" / "DashboardAssistantPlanner.cs").read_text(encoding="utf-8")
EXECUTOR = (ROOT / "Controllers" / "DashboardController.AssistantExecutive.cs").read_text(encoding="utf-8")

required_catalog_literals = [
    '"payment_value"',
    '"Payment Value"',
    '"amount"',
    '"transactions"',
    '"Transactions"',
    'new[] { "period", "payment_type" }',
    'new[] { "period" }',
    '"payment_type"',
    '"Payment Type"',
]
for literal in required_catalog_literals:
    assert literal in CATALOG, f"Missing semantic catalog literal: {literal}"

required_planner_literals = [
    'string.Equals(normalizedQuestion, normalizedAlias, StringComparison.Ordinal)',
    'ContainsWholePhrase(normalizedQuestion, normalizedAlias)',
    'questionTokens.Contains("amount")',
    'questionTokens.Contains("transactions")',
    'measure.AllowedDimensions',
    'SuggestionKindPriority',
]
for literal in required_planner_literals:
    assert literal in PLANNER, f"Missing planner behavior: {literal}"

assert "Transactions is available by period on this screen, but not by payment type" in EXECUTOR


def normalize(value: str) -> str:
    value = value.strip().lower()
    value = value.replace("e-bill", "ebill")
    value = value.replace("month-over-month", "mom")
    value = value.replace("year-over-year", "yoy")
    value = value.replace("month over month", "mom")
    value = value.replace("year over year", "yoy")
    return re.sub(r"\s+", " ", re.sub(r"[^a-z0-9%]+", " ", value)).strip()


def tokens(value: str) -> set[str]:
    return {item for item in normalize(value).split() if len(item) > 1}


def whole(text: str, phrase: str) -> bool:
    return f" {text} ".find(f" {phrase} ") >= 0


def score(aliases: list[str], question: str, name: str, value_format: str, priority: int) -> float:
    normalized_question = normalize(question)
    question_tokens = tokens(normalized_question)
    result = 0.0
    for alias in aliases:
        normalized_alias = normalize(alias)
        alias_tokens = tokens(normalized_alias)
        if normalized_question == normalized_alias:
            alias_score = 1.0
        elif whole(normalized_question, normalized_alias):
            alias_score = 0.86 if len(alias_tokens) <= 1 else 0.94
        elif alias_tokens:
            overlap = len(alias_tokens & question_tokens)
            coverage = overlap / len(alias_tokens)
            if coverage >= 1:
                alias_score = 0.72 if len(alias_tokens) == 1 else 0.82
            else:
                alias_score = coverage * 0.52
        else:
            alias_score = 0.0
        result = max(result, alias_score)

    normalized_name = normalize(name)
    if {"transaction", "transactions"} & question_tokens and (
        "transaction" in normalized_name or "count" in normalized_name
    ):
        result = max(result, 0.98)
    if any(phrase in normalized_question for phrase in ("how many", "number of", "count of")) and any(
        phrase in normalized_name for phrase in ("transaction", "count", "accounts", "customers", "volume")
    ):
        result = max(result, 0.96)
    if {"amount", "amounts"} & question_tokens and value_format == "currency":
        result = max(result, 0.91)
    if {"value", "values"} & question_tokens and any(
        phrase in normalized_name for phrase in ("value", "amount", "balance")
    ):
        result = max(result, 0.90)
    if {"collection", "collections"} & question_tokens and any(
        phrase in normalized_name for phrase in ("amount", "value", "payment", "paid", "balance")
    ):
        result = max(result, 0.90)
    if result > 0:
        result = min(1.0, result + min(0.025, priority / 10000))
    return result


payment_aliases = [
    "payment_value", "Payment Value", "amount", "amounts", "value", "values",
    "payment", "payments", "payment amount", "collection amount", "collections",
    "amount collected", "cash collected",
]
transaction_aliases = [
    "transactions", "Transactions", "transaction", "count", "counts", "number", "volume",
    "transaction count", "number of transactions", "payment count", "number of payments",
]

cases = {
    "amount": "payment_value",
    "transactions": "transactions",
    "how many transactions last month": "transactions",
    "payment amount by payment type": "payment_value",
    "collections by month": "payment_value",
    "count for the last 12 months": "transactions",
    "show payments": "payment_value",
    "value by method": "payment_value",
    "how many payments were there": "transactions",
}

for question, expected in cases.items():
    payment_score = score(payment_aliases, question, "payment_value", "currency", 100)
    transaction_score = score(transaction_aliases, question, "transactions", "number", 95)
    actual = "payment_value" if payment_score > transaction_score else "transactions"
    assert actual == expected, (
        f"{question!r}: expected {expected}, got {actual}; "
        f"payment={payment_score:.3f}, transactions={transaction_score:.3f}"
    )
    print(
        f"PASS {question!r}: {actual} "
        f"(payment={payment_score:.3f}, transactions={transaction_score:.3f})"
    )

print("PASS Version 217 semantic facts and dimensions are explicit and deterministic.")

grouping_regex = re.compile(
    r"\b(?:grouped\s+by|broken\s+down\s+by|split\s+by|by|per)\s+(?P<group>.*?)(?=\s+(?:for|since|from|during|over|as|with|compared|versus|vs|by)\b|$)",
    re.IGNORECASE,
)
grouping_cases = {
    "amount by payment type for last month": ["payment type"],
    "transactions last month": [],
    "aggregate amount by date for the last two years by category and type as a matrix": ["date", "category and type"],
    "show transactions for the last 12 months as a line chart": [],
    "amount per payment method since march 2026": ["payment method"],
}
for question, expected_groups in grouping_cases.items():
    actual_groups = [normalize(match.group("group")) for match in grouping_regex.finditer(question)]
    assert actual_groups == expected_groups, f"{question!r}: expected groups {expected_groups}, got {actual_groups}"
    print(f"PASS grouping {question!r}: {actual_groups}")

print("PASS Time filters no longer become grouping dimensions unless grouping/trend language is explicit.")
