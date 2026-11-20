# 4. Form redesign recommendations (PC-011-A v2)

The system already works on today's form. These changes make most sheets **auto-approve with no typing at all**.
They are ordered by impact.

1. **Shift as three tick boxes** `☐ 6-2  ☐ 2-10  ☐ 10-6` instead of handwriting "2/10".
2. **Hourly reason codes as tick boxes** (e.g. R1 Mould change, R2 Power failure, R3 Steam low, R4 Material
   shortage, R5 Breakdown, R6 Other) in each hour row. Handwritten Sinhala reasons are the hardest thing for any
   OCR. Keep a small free-text line for "Other".
3. **Comb boxes (one box per digit)** for EPF No, Press No, Target, Total Production and the date
   (`DD MM YY`). Digit-per-box handwriting is read far more reliably and shows exactly what was written.
4. **Checklist with two boxes per item**: `OK ☐  Not OK ☐`. Today a tick can't say whether the check passed.
5. **QR code / barcode** with form code + version (and optionally a pre-printed serial number). The system can then
   pick the template automatically and detect missing/duplicate sheets.
6. **Four solid black corner squares** (≈8 mm) just outside the border, for even more robust alignment of
   folded or phone-photographed sheets (`alignment.method: Fiducials`).
7. **Item / tyre**: pre-print it from the production plan, or use an item-code comb box instead of free text.
8. **Hourly number cells**: keep one number per cell, written large and centred, leaving cells **empty** (not "0" or
   "-") when nothing was produced, so empty cells are skipped without any reading. If the number in a cell is
   always the press cavity count (e.g. always 6), a **tick** instead of a number would be read with ~100% accuracy
   and no AI (the system supports this too: extractor `OmrTally`, `unitsPerTick` from `dsi.Machine`).
9. Print in **black on white**. The grey shaded header bands make thresholding harder; light colour (drop-out ink)
   for printed labels is even better.

Each change is only a new template version (see [03-customizing.md](03-customizing.md#b-capture-a-new-box-from-the-paper-form)).
Reports keep working because field keys stay the same.
