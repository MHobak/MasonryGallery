-- Earlier builds never found EXIF (they looked for APP1 at byte 0, where every JPEG has SOI),
-- so DateTaken fell back to mtime and rotated photos kept their stored width/height.
-- Re-read every image header once; unchanged files are otherwise never probed again.
UPDATE Media SET ProbeState = 0 WHERE Kind = 0;
