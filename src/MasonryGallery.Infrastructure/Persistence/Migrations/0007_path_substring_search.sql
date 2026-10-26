-- Substring search over the whole relative path (folders and file name): "kpop" finds "IdiotsKPOP/clip.webm",
-- which the word-prefix index (MediaFts) can't, since it only matches the start of words.
-- The trigram tokenizer needs at least 3 characters; shorter search terms still use MediaFts.
CREATE VIRTUAL TABLE IF NOT EXISTS MediaPathFts USING fts5(RelPath, content='Media', content_rowid='Id', tokenize='trigram');

CREATE TRIGGER IF NOT EXISTS Media_path_ai AFTER INSERT ON Media BEGIN
  INSERT INTO MediaPathFts (rowid, RelPath) VALUES (new.Id, new.RelPath);
END;

CREATE TRIGGER IF NOT EXISTS Media_path_ad AFTER DELETE ON Media BEGIN
  INSERT INTO MediaPathFts (MediaPathFts, rowid, RelPath) VALUES ('delete', old.Id, old.RelPath);
END;

CREATE TRIGGER IF NOT EXISTS Media_path_au AFTER UPDATE OF RelPath ON Media
WHEN old.RelPath IS NOT new.RelPath BEGIN
  INSERT INTO MediaPathFts (MediaPathFts, rowid, RelPath) VALUES ('delete', old.Id, old.RelPath);
  INSERT INTO MediaPathFts (rowid, RelPath) VALUES (new.Id, new.RelPath);
END;

-- Index the rows that already exist (a few seconds for a few hundred thousand files).
INSERT INTO MediaPathFts (MediaPathFts) VALUES ('rebuild');
