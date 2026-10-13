import { describe, expect, test } from "vitest";

import { PRESENTATION_TEXT_MAX_LENGTH, sanitizePresentationText } from "../src/sanitize.js";

describe("sanitizePresentationText", () => {
  test("removes terminal, control, bidi, and line-breaking sequences", () => {
    const value = "safe\u001b[31m red\u001b[0m\u001b]8;;https://secret.example\u0007link\u001b]8;;\u0007\n\t\u202Eend";

    expect(sanitizePresentationText(value)).toBe("safe redlink end");
  });

  test("drops unpaired surrogates while retaining valid pairs", () => {
    expect(sanitizePresentationText(`a\ud800b\udc00c\ud83d\ude80d`)).toBe("abc🚀d");
  });

  test("bounds output without splitting code points", () => {
    const value = `x${"🚀".repeat(PRESENTATION_TEXT_MAX_LENGTH)}tail`;
    const result = sanitizePresentationText(value);

    expect(Array.from(result)).toHaveLength(PRESENTATION_TEXT_MAX_LENGTH);
    expect(result.endsWith("🚀")).toBe(true);
  });
});
