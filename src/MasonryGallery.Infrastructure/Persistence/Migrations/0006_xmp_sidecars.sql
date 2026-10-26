-- XMP sidecars (photo.jpg.xmp / photo.xmp). The stored write time decides when a sidecar is read again.
ALTER TABLE Media ADD COLUMN SidecarPath TEXT;
ALTER TABLE Media ADD COLUMN SidecarModifiedUtc INTEGER;

-- Hierarchical keywords ("Artist|name") keep their parent as the tag's category.
ALTER TABLE Tag ADD COLUMN Category TEXT;

-- 0 = added in the app, 1 = read from the sidecar. Re-reading a sidecar replaces only its own rows.
ALTER TABLE MediaTag ADD COLUMN Source INTEGER NOT NULL DEFAULT 0;

-- Sidecar details that aren't tags. One row per item that has a readable sidecar.
CREATE TABLE IF NOT EXISTS Sidecar (
  MediaId INTEGER PRIMARY KEY REFERENCES Media(Id) ON DELETE CASCADE,
  Creator TEXT,
  Source TEXT,
  Title TEXT,
  Description TEXT,
  Rating INTEGER
);
