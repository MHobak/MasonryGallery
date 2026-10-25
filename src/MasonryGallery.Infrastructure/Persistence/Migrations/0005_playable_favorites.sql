-- Whether the WebView can likely play a video's codec (NULL for images / unknown).
-- The lightbox offers "open externally" when it is 0.
ALTER TABLE Media ADD COLUMN Playable INTEGER;

-- Re-probe videos once: fills Playable, and earlier builds missed rotation stored as a display matrix.
UPDATE Media SET ProbeState = 0 WHERE Kind = 1;

CREATE INDEX IF NOT EXISTS IX_Media_Favorite ON Media (Favorite) WHERE Favorite = 1;
