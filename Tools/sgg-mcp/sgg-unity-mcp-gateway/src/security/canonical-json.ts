export function canonicalJsonStringify(value: unknown): string {
  return JSON.stringify(validateAndSortJsonValue(value, new Set()));
}

function validateAndSortJsonValue(value: unknown, ancestors: Set<object>): unknown {
  if (value === null || typeof value === "string" || typeof value === "boolean") {
    return value;
  }
  if (typeof value === "number") {
    if (!Number.isFinite(value)) {
      throw new TypeError("Non-finite numbers are not canonical JSON values");
    }
    return value;
  }
  if (typeof value !== "object") {
    throw new TypeError(`Values of type ${typeof value} are not canonical JSON values`);
  }

  if (ancestors.has(value)) {
    throw new TypeError("Circular values are not canonical JSON values");
  }
  ancestors.add(value);
  try {
    if (Array.isArray(value)) {
      const sorted: unknown[] = [];
      for (let index = 0; index < value.length; index++) {
        if (!Object.hasOwn(value, index)) {
          throw new TypeError("Sparse arrays are not canonical JSON values");
        }
        sorted.push(validateAndSortJsonValue(value[index], ancestors));
      }
      return sorted;
    }

    const prototype = Object.getPrototypeOf(value) as unknown;
    if (prototype !== Object.prototype && prototype !== null) {
      throw new TypeError("Non-plain objects are not canonical JSON values");
    }
    const ownKeys = Reflect.ownKeys(value);
    if (ownKeys.some((key) => typeof key !== "string") || ownKeys.some((key) => !Object.prototype.propertyIsEnumerable.call(value, key))) {
      throw new TypeError("Canonical JSON objects require enumerable string keys");
    }
    const entries = Object.entries(value)
      .sort(([left], [right]) => compareJsonKeys(left, right))
      .map(([key, child]) => [key, validateAndSortJsonValue(child, ancestors)] as const);
    return Object.fromEntries(entries);
  } finally {
    ancestors.delete(value);
  }
}

function compareJsonKeys(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0;
}
