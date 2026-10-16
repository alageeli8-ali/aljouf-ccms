-- V007: وصف إنجليزي لمفاتيح الإعداد لواجهة اللغة الإنجليزية (NFR-USA-01)
ALTER TABLE config.feature_flags ADD COLUMN description_en text;

UPDATE config.feature_flags f SET description_en = v.d FROM (VALUES
  ('AI_ENABLED', 'Master switch for all AI features; disabling it disables everything beneath it'),
  ('AI_MINUTES_SUMMARY_ENABLED', 'Automatic summaries of memos and minutes, shown as suggestions'),
  ('AI_DECISION_EXTRACTION_ENABLED', 'Suggest decisions and recommendations from the minutes text'),
  ('AI_TASK_EXTRACTION_ENABLED', 'Suggest tasks, assignees and due dates from resolutions'),
  ('AI_SMART_SEARCH_ENABLED', 'Semantic search and archive assistant, limited to the user''s access'),
  ('AI_AUDIO_TRANSCRIPTION_ENABLED', 'Transcribe meeting recordings with documented attendee consent'),
  ('E_SIGNATURE_ENABLED', 'Master switch for the signature module; the system uses in-app attestation when disabled'),
  ('E_SIGNATURE_PROVIDER', 'none: not selected; sandbox: test simulator only; national_provider: accredited provider (after contracting)'),
  ('E_SIGNATURE_METHOD', 'remote_certificate: remote digital signature with the provider''s certificate; otp_confirmation: OTP-confirmed e-signature'),
  ('E_SIGNATURE_MEMBER_REQUIRED', 'Present members must sign electronically before approval'),
  ('E_SIGNATURE_CHAIR_REQUIRED', 'The chair signs the approved copy electronically'),
  ('E_SIGNATURE_SECRETARY_REQUIRED', 'The secretary signs the minutes electronically as their author'),
  ('E_SIGNATURE_SEQUENTIAL', 'Sign in a defined order instead of in parallel'),
  ('E_SIGNATURE_PDF_OUTPUT', 'Produce a PAdES-signed PDF'),
  ('E_SIGNATURE_REAUTH_REQUIRED', 'Signers must re-authenticate before every signature'),
  ('EXTERNAL_PORTAL_ENABLED', 'Lets external entity representatives view and update their entity''s tasks'),
  ('EMAIL_INTEGRATION_ENABLED', 'Send notifications by email; when disabled, messages are logged in the outbox and not sent'),
  ('CORRESPONDENCE_INTEGRATION_ENABLED', 'Create outgoing letters to external entities in the correspondence system'),
  ('SSO_ENABLED', 'Sign in with Microsoft Entra ID / Active Directory')
) AS v(k, d) WHERE f.key = v.k;

ALTER TABLE config.feature_flags ALTER COLUMN description_en SET NOT NULL;
