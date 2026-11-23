ALTER TABLE Users ADD COLUMN pass_v2 VARCHAR(256) NOT NULL DEFAULT '';

BEGIN TRANSACTION;
UPDATE Users
    SET pass_v2 = regexp_replace(pass_argon2id, '=+(\$|$)', '\1', 'g')
    WHERE pass_argon2id LIKE '$argon2id$%';
COMMIT;

ALTER TABLE Users DROP COLUMN pass_argon2id;
