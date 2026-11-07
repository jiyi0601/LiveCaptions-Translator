# Synthetic speech fixture

long-speech.json records the changing Windows Live Captions text produced by a fixed English passage synthesized with Microsoft Zira Desktop, English (United States), rate 1. Times are seconds relative to the first changed caption. It contains no meeting recording, account data, or credentials.

The passage describes an experiment, a repeat measurement, and a bilingual reading window. Windows revises earlier completed text (including "second" to "2nd") while extending the final sentence. The regression test checks that replayed fragments reproduce the final recognized caption exactly once, with stable paragraph grouping and the balanced length limit.

The trace is only test input. The production application does not write this diagnostic trace.
