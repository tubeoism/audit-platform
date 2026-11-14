# Rebuilding a laptop's copy of an engagement

The SQLite replica holds only what the master has decided: the pulled change log (`replica_log`), the working papers folded
from it (`documents`) and the pull cursor (`sync_state`). The author's unsent work is separate (the outbox), so the replica
can always be thrown away and pulled again from sequence 0.

## When sync of an engagement stops

| Cause | Detected as | Message to the user |
|---|---|---|
| A gap, an unreadable change, or a change the local reducer refuses (including a sign-off hash that does not match) | `ReplicaDivergedException` on apply | "Bản trên máy không khớp với máy chủ và đã dừng đồng bộ..." |
| The master has *other* changes at the positions the laptop holds (master restored from an older backup, then grown past the laptop) | Hub error `replica_diverged` on pull: the pull cursor carries the id of the laptop's last change (`SyncCursor.AnchorChangeId`, read from its own log) and the master compares it with the change it has at that position. A client that sends no id skips the check |
| The laptop is ahead of the master (master restored from an older backup) | Hub error `replica_ahead` on pull | "Máy chủ đang ở thời điểm cũ hơn bản trên máy bạn... Báo Admin trước khi dựng lại" |

Sync of that engagement then stays stopped (other engagements continue) until the user rebuilds. The engagement screen shows
the reason and a "Dựng lại từ máy chủ" button.

## What the rebuild does (`ReplicaRebuild`, `SyncLoop.RebuildReplicaAsync`)

1. Copies the whole local database aside with `VACUUM INTO` to `<data>/backups/replica-<code>-<utc time>-<id>.db`. Nothing the
   laptop holds is lost even if the master is the one that is behind; the path is shown to the user.
2. In one transaction deletes that engagement's `replica_log`, `documents` and `sync_state` rows.
3. Clears the stop and wakes the loop; the next round pulls everything again. Unsent outbox rows stay; those the master
   already has settle when their change comes back, the rest are pushed.

Evidence files and their upload state are not touched.

## After a master restore

If the master is restored from a backup, changes made since that backup exist only on the laptops. Do not let users rebuild
before the Admin has decided how to recover them: the `backups/` copy holds the later `replica_log`, from which the changes can
be pushed again.
