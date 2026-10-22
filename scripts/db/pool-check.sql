/*
  StockAccount - connection pool / blocking diagnostics (read-only, safe to run any time).

  Local : sqlcmd -S .\SQLEXPRESS -E -C -I -W -i scripts\db\pool-check.sql
  Docker: docker compose cp scripts/db/pool-check.sql mssql:/tmp/pool-check.sql
          docker compose exec mssql bash -c 'SQLCMDPASSWORD=$MSSQL_SA_PASSWORD /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -I -W -i /tmp/pool-check.sql'

  What to look for:
    1. app_sessions per program close to "Max Pool Size"    -> pool exhaustion risk (API logs: "Timeout expired ... obtaining a connection from the pool").
    2. sleeping sessions with open_tran > 0                    -> leaked transaction (a request did not commit/dispose).
    3. blocked requests / head blockers                        -> lock contention (invoice approval should hold locks for milliseconds).
    4. THREADPOOL waits > 0                                    -> server worker starvation (too many concurrent requests for the CPU/edition).
    5. is_auto_close_on = 1                                    -> Express default; closes the DB when the pool drains. Turn it OFF.
  Program names: set "Application Name=StockAccount.Api" in the connection string, otherwise SqlClient reports
  "Core Microsoft SqlClient Data Provider".
*/
SET NOCOUNT ON;

PRINT '== 1. Server capacity ==';
SELECT  @@SERVERNAME                                        AS server_name,
        CONVERT(nvarchar(128), SERVERPROPERTY('Edition'))   AS edition,
        @@MAX_CONNECTIONS                                   AS max_connections,
        (SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE is_user_process = 1) AS user_sessions,
        si.cpu_count,
        si.max_workers_count,
        (SELECT SUM(current_workers_count) FROM sys.dm_os_schedulers WHERE status = 'VISIBLE ONLINE') AS current_workers,
        (SELECT SUM(work_queue_count) FROM sys.dm_os_schedulers WHERE status = 'VISIBLE ONLINE')      AS queued_tasks
FROM sys.dm_os_sys_info AS si;

PRINT '== 2. Sessions by program / host / login / database (pool usage) ==';
SELECT  ISNULL(s.program_name, N'')                             AS program_name,
        ISNULL(s.host_name, N'')                                AS host_name,
        s.login_name,
        DB_NAME(s.database_id)                                  AS database_name,
        COUNT(*)                                                AS app_sessions,
        SUM(CASE WHEN s.status = 'running' THEN 1 ELSE 0 END)   AS running,
        SUM(CASE WHEN s.status = 'sleeping' THEN 1 ELSE 0 END)  AS sleeping,
        SUM(s.open_transaction_count)                           AS open_tran,
        MIN(s.login_time)                                       AS oldest_login,
        MAX(s.last_request_end_time)                            AS last_activity
FROM sys.dm_exec_sessions AS s
WHERE s.is_user_process = 1
GROUP BY s.program_name, s.host_name, s.login_name, s.database_id
ORDER BY app_sessions DESC;

PRINT '== 3. Idle sessions holding an open transaction (possible leak) ==';
SELECT  s.session_id, s.program_name, s.host_name, s.login_name, DB_NAME(s.database_id) AS database_name,
        s.open_transaction_count, s.last_request_end_time,
        DATEDIFF(SECOND, s.last_request_end_time, SYSDATETIME()) AS idle_seconds,
        t.text AS last_sql
FROM sys.dm_exec_sessions AS s
JOIN sys.dm_exec_connections AS c ON c.session_id = s.session_id
OUTER APPLY sys.dm_exec_sql_text(c.most_recent_sql_handle) AS t
WHERE s.is_user_process = 1 AND s.status = 'sleeping' AND s.open_transaction_count > 0
ORDER BY idle_seconds DESC;

PRINT '== 4. Blocked requests ==';
SELECT  r.session_id, r.blocking_session_id, DB_NAME(r.database_id) AS database_name,
        r.status, r.command, r.wait_type, r.wait_time AS wait_ms, r.wait_resource,
        s.program_name, s.host_name,
        SUBSTRING(t.text, r.statement_start_offset / 2 + 1,
                  (CASE r.statement_end_offset WHEN -1 THEN DATALENGTH(t.text) ELSE r.statement_end_offset END - r.statement_start_offset) / 2 + 1) AS current_statement
FROM sys.dm_exec_requests AS r
JOIN sys.dm_exec_sessions AS s ON s.session_id = r.session_id
OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) AS t
WHERE r.blocking_session_id <> 0
ORDER BY r.wait_time DESC;

PRINT '== 5. Head blockers (block others, not blocked themselves) ==';
SELECT  s.session_id, s.status, s.program_name, s.host_name, s.login_name, s.open_transaction_count,
        (SELECT COUNT(*) FROM sys.dm_exec_requests AS r2 WHERE r2.blocking_session_id = s.session_id) AS blocked_count,
        t.text AS last_sql
FROM sys.dm_exec_sessions AS s
LEFT JOIN sys.dm_exec_requests AS r ON r.session_id = s.session_id
LEFT JOIN sys.dm_exec_connections AS c ON c.session_id = s.session_id
OUTER APPLY sys.dm_exec_sql_text(c.most_recent_sql_handle) AS t
WHERE s.session_id IN (SELECT blocking_session_id FROM sys.dm_exec_requests WHERE blocking_session_id <> 0)
  AND ISNULL(r.blocking_session_id, 0) = 0;

PRINT '== 6. Worker starvation and lock waits since startup ==';
SELECT  wait_type, waiting_tasks_count, wait_time_ms, max_wait_time_ms
FROM sys.dm_os_wait_stats
WHERE wait_type IN ('THREADPOOL', 'LCK_M_U', 'LCK_M_X', 'LCK_M_S', 'LCK_M_IX', 'RESOURCE_SEMAPHORE', 'WRITELOG')
ORDER BY wait_time_ms DESC;

PRINT '== 7. Database options relevant to pooling ==';
SELECT  name, state_desc, user_access_desc, recovery_model_desc, log_reuse_wait_desc,
        is_auto_close_on, is_auto_shrink_on, is_read_committed_snapshot_on
FROM sys.databases
WHERE name LIKE N'StockAccount%';
