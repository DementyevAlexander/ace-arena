import { describe, expect, it } from "vitest";

import { canonicalJsonStringify } from "../src/security/canonical-json.js";

describe("canonicalJsonStringify", () => {
  it("sorts every object by locale-independent UTF-16 code unit order", () => {
    expect(canonicalJsonStringify({ z: 1, "ä": 2, A: 3, a: { "😀": 1, "\uffff": 2 } })).toBe(
      '{"A":3,"a":{"😀":1,"￿":2},"z":1,"ä":2}',
    );
  });

  it("preserves array order and JSON primitive encoding", () => {
    expect(canonicalJsonStringify({ values: [3, "line\nquote\"", true, false, null, -0, 1.5] })).toBe(
      '{"values":[3,"line\\nquote\\\"",true,false,null,0,1.5]}',
    );
  });

  it("is independent of object insertion order", () => {
    expect(canonicalJsonStringify({ b: 2, a: { d: 4, c: 3 } })).toBe(canonicalJsonStringify({ a: { c: 3, d: 4 }, b: 2 }));
  });

  it("rejects undefined, functions, symbols, BigInt and non-finite numbers at any depth", () => {
    for (const invalid of [undefined, () => undefined, Symbol("x"), 1n, Number.NaN, Infinity, -Infinity]) {
      expect(() => canonicalJsonStringify(invalid)).toThrow(TypeError);
      expect(() => canonicalJsonStringify({ invalid })).toThrow(TypeError);
      expect(() => canonicalJsonStringify([invalid])).toThrow(TypeError);
    }
  });

  it("rejects cycles, sparse arrays and non-plain objects", () => {
    const circular: Record<string, unknown> = {};
    circular.self = circular;
    expect(() => canonicalJsonStringify(circular)).toThrow("Circular value");
    expect(() => canonicalJsonStringify(new Date())).toThrow("Non-plain object");
    expect(() => canonicalJsonStringify(new Map())).toThrow("Non-plain object");
    expect(() => canonicalJsonStringify(Array(1))).toThrow("Sparse array");
  });

  it("accepts null-prototype plain objects and rejects symbol or hidden keys", () => {
    const plain = Object.assign(Object.create(null) as Record<string, unknown>, { b: 2, a: 1 });
    expect(canonicalJsonStringify(plain)).toBe('{"a":1,"b":2}');
    const symbolKey = { a: 1, [Symbol("hidden")]: 2 };
    expect(() => canonicalJsonStringify(symbolKey)).toThrow("enumerable string keys");
    const hidden = { a: 1 };
    Object.defineProperty(hidden, "hidden", { value: 2 });
    expect(() => canonicalJsonStringify(hidden)).toThrow("enumerable string keys");
  });

  it("allows the same non-circular object to appear more than once", () => {
    const child = { b: 2, a: 1 };
    expect(canonicalJsonStringify([child, child])).toBe('[{"a":1,"b":2},{"a":1,"b":2}]');
  });
});
