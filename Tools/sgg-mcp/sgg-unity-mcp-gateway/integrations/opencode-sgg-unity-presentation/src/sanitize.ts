const ANSI_OSC_PATTERN = /(?:\u001B\]|\u009D)[\s\S]*?(?:\u0007|\u001B\\|\u009C)/gu;
const ANSI_CSI_PATTERN = /(?:\u001B\[|\u009B)[0-?]*[ -/]*[@-~]/gu;
const ANSI_ESCAPE_PATTERN = /\u001B[@-_]/gu;
const BIDI_CONTROL_PATTERN = /[\u061C\u200E\u200F\u202A-\u202E\u2066-\u2069]/gu;
const CONTROL_PATTERN = /[\u0000-\u001F\u007F-\u009F]/gu;
const LINE_BREAK_PATTERN = /[\r\n\t]+/gu;
const WHITESPACE_PATTERN = /\s+/gu;

export const PRESENTATION_TEXT_MAX_LENGTH = 160;

function removeInvalidSurrogates(value: string): string {
  let result = "";
  for (let index = 0; index < value.length; index += 1) {
    const code = value.charCodeAt(index);
    if (code >= 0xd800 && code <= 0xdbff) {
      const next = value.charCodeAt(index + 1);
      if (next >= 0xdc00 && next <= 0xdfff) {
        result += value[index] ?? "";
        result += value[index + 1] ?? "";
        index += 1;
      }
      continue;
    }
    if (code >= 0xdc00 && code <= 0xdfff) continue;
    result += value[index] ?? "";
  }
  return result;
}

export function sanitizePresentationText(value: string, maxLength = PRESENTATION_TEXT_MAX_LENGTH): string {
  const boundedLength = Number.isSafeInteger(maxLength) && maxLength > 0
    ? Math.min(maxLength, PRESENTATION_TEXT_MAX_LENGTH)
    : PRESENTATION_TEXT_MAX_LENGTH;
  const normalized = removeInvalidSurrogates(value)
    .replace(ANSI_OSC_PATTERN, "")
    .replace(ANSI_CSI_PATTERN, "")
    .replace(ANSI_ESCAPE_PATTERN, "")
    .replace(LINE_BREAK_PATTERN, " ")
    .replace(BIDI_CONTROL_PATTERN, "")
    .replace(CONTROL_PATTERN, "")
    .replace(WHITESPACE_PATTERN, " ")
    .trim();
  return Array.from(normalized).slice(0, boundedLength).join("");
}
