// ISO 3166-1 alpha-2 -> regional indicator flag emoji (e.g. "CL" -> 🇨🇱). Falls back to no flag
// for anything that isn't exactly 2 letters (this catalog's other categories use longer,
// non-country values, and country codes here are always uppercase 2-letter).
export function countryFlag(countryCode: string): string {
  const upper = countryCode.toUpperCase();
  if (!/^[A-Z]{2}$/.test(upper)) return "";
  const codePoints = [...upper].map((c) => 127397 + c.charCodeAt(0));
  return String.fromCodePoint(...codePoints);
}
