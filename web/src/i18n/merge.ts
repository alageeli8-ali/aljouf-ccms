type Obj = Record<string, unknown>;

/** دمج عميق لقواميس الترجمة: الإضافات تُلحق بالأقسام القائمة دون تكرارها. */
export function mergeDeep<A extends Obj, B extends Obj>(base: A, extra: B): A & B {
  const out: Obj = { ...base };
  for (const [k, v] of Object.entries(extra)) {
    const cur = out[k];
    out[k] = cur && typeof cur === 'object' && v && typeof v === 'object' ? mergeDeep(cur as Obj, v as Obj) : v;
  }
  return out as A & B;
}
