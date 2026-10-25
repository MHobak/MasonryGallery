-- MediaFts is an external-content table: it only sees rows the triggers hand it.
CREATE TRIGGER IF NOT EXISTS Media_ai AFTER INSERT ON Media BEGIN
  INSERT INTO MediaFts (rowid, FileName, RelPath) VALUES (new.Id, new.FileName, new.RelPath);
END;

CREATE TRIGGER IF NOT EXISTS Media_ad AFTER DELETE ON Media BEGIN
  INSERT INTO MediaFts (MediaFts, rowid, FileName, RelPath) VALUES ('delete', old.Id, old.FileName, old.RelPath);
END;

-- Rescans rewrite FileName on every row; only reindex when the text actually changed.
CREATE TRIGGER IF NOT EXISTS Media_au AFTER UPDATE OF FileName, RelPath ON Media
WHEN old.FileName IS NOT new.FileName OR old.RelPath IS NOT new.RelPath BEGIN
  INSERT INTO MediaFts (MediaFts, rowid, FileName, RelPath) VALUES ('delete', old.Id, old.FileName, old.RelPath);
  INSERT INTO MediaFts (rowid, FileName, RelPath) VALUES (new.Id, new.FileName, new.RelPath);
END;

-- Index rows written before these triggers existed.
INSERT INTO MediaFts (MediaFts) VALUES ('rebuild');
