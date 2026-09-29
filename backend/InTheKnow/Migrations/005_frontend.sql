-- 005: which view the signed-in home page shows (specs/005 data-model.md, FR-046).
ALTER TABLE account ADD COLUMN home_page text NOT NULL DEFAULT 'changes'
    CHECK (home_page IN ('changes', 'combined'));
