-- Earlier builds failed most video thumbnails (ffmpeg could not infer a format from a .tmp name)
-- and stored the rest as JPEG under .webp. Queue every video for the fixed generator.
UPDATE Media SET ThumbState = 0 WHERE Kind = 1;
