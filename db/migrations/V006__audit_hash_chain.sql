-- V006: سلسلة بصمات التدقيق داخل القاعدة (FR-AUD-02).
-- البصمة تُحسب في القاعدة على التمثيل النصي القانوني لـ jsonb، فلا تتأثر بترتيب المفاتيح في التطبيق.
-- chain_seq يُمنح تحت قفل استشاري، فيعكس ترتيب الالتزام الفعلي حتى مع المعاملات المتزامنة.

ALTER TABLE audit.audit_events ADD COLUMN chain_seq bigint UNIQUE;

CREATE OR REPLACE FUNCTION audit.row_digest(e audit.audit_events, prev text) RETURNS text
LANGUAGE sql IMMUTABLE AS $$
  SELECT encode(sha256(convert_to(concat_ws('|',
    prev,
    e.chain_seq::text,
    to_char(e.occurred_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US'),
    coalesce(e.actor_user_id::text, ''),
    coalesce(e.on_behalf_of_user_id::text, ''),
    e.module, e.action, e.entity_type,
    coalesce(e.entity_id, ''),
    e.outcome,
    coalesce(e.before::text, ''),
    coalesce(e.after::text, '')), 'UTF8')), 'hex')
$$;

CREATE OR REPLACE FUNCTION audit.chain_before_insert() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
  last_seq  bigint;
  last_hash text;
BEGIN
  PERFORM pg_advisory_xact_lock(7231002);
  SELECT chain_seq, hash INTO last_seq, last_hash FROM audit.audit_events ORDER BY chain_seq DESC NULLS LAST LIMIT 1;
  NEW.chain_seq := coalesce(last_seq, 0) + 1;
  NEW.prev_hash := coalesce(last_hash, 'GENESIS');
  NEW.hash := audit.row_digest(NEW, NEW.prev_hash);
  RETURN NEW;
END $$;

CREATE TRIGGER audit_chain BEFORE INSERT ON audit.audit_events FOR EACH ROW EXECUTE FUNCTION audit.chain_before_insert();

-- يعيد عدد السجلات المفحوصة وأول تسلسل مكسور (NULL إن كانت السلسلة سليمة)
CREATE OR REPLACE FUNCTION audit.verify_chain() RETURNS TABLE (checked bigint, first_broken_seq bigint)
LANGUAGE plpgsql STABLE AS $$
DECLARE
  r audit.audit_events;
  prev text := 'GENESIS';
  expected_seq bigint := 1;
BEGIN
  checked := 0;
  first_broken_seq := NULL;
  FOR r IN SELECT * FROM audit.audit_events ORDER BY chain_seq LOOP
    checked := checked + 1;
    IF r.chain_seq <> expected_seq OR r.prev_hash <> prev OR r.hash <> audit.row_digest(r, prev) THEN
      first_broken_seq := r.chain_seq;
      RETURN NEXT;
      RETURN;
    END IF;
    prev := r.hash;
    expected_seq := expected_seq + 1;
  END LOOP;
  RETURN NEXT;
END $$;
