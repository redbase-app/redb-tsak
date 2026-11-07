-- Quartz.NET 3.22 schema migration for redb.Tsak (postgres), for the administrator.
--
-- The Tsak worker adds the columns below itself at start (QuartzSchemaInitializer), under a lock, when its
-- database role may ALTER the Quartz tables. Run this script when it may not (the worker then logs a warning
-- naming this file), or to apply the index alignment, which the worker never does on its own.
--
--   3.17 MISFIRE_ORIG_FIRE_TIME: ScheduledFireTimeUtc keeps the time a misfired trigger was due.
--   3.18 EXECUTION_GROUP: the per-node limit of execution groups is applied in the acquire query.
--   3.19 PREFERRED_NODE, PREFERRED_NODE_AUTO: node affinity in a cluster (both or neither).
--   3.20 index alignment: performance only; drops and creates indexes, so run it in a quiet window.
--
-- Every statement checks first, so the script is safe to run more than once. Columns are looked up in current_schema(), where Tsak creates the tables.
-- Tables carry the QRTZ_ prefix Tsak configures. Taken from the Quartz.NET 3.x migrations (Apache License 2.0),
-- database/migrations/3.17 to 3.20 of github.com/quartznet/quartznet.
--
-- !! FIRST RUN IN A TEST ENVIRONMENT AGAINST A COPY OF YOUR PRODUCTION DATABASE !!

-- ==== 3.17 add misfire orig fire time ====

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                 WHERE table_schema = current_schema() AND table_name = 'qrtz_triggers' AND column_name = 'misfire_orig_fire_time') THEN
    ALTER TABLE qrtz_triggers ADD COLUMN misfire_orig_fire_time bigint null;
  END IF;
END $$;

-- ==== 3.18 add execution group ====

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                 WHERE table_schema = current_schema() AND table_name = 'qrtz_triggers' AND column_name = 'execution_group') THEN
    ALTER TABLE qrtz_triggers ADD COLUMN execution_group varchar(200) null;
  END IF;
END $$;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                 WHERE table_schema = current_schema() AND table_name = 'qrtz_fired_triggers' AND column_name = 'execution_group') THEN
    ALTER TABLE qrtz_fired_triggers ADD COLUMN execution_group varchar(200) null;
  END IF;
END $$;

-- ==== 3.19 add preferred node ====

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                 WHERE table_schema = current_schema() AND table_name = 'qrtz_triggers' AND column_name = 'preferred_node') THEN
    ALTER TABLE qrtz_triggers ADD COLUMN preferred_node varchar(200) null;
  END IF;
END $$;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                 WHERE table_schema = current_schema() AND table_name = 'qrtz_triggers' AND column_name = 'preferred_node_auto') THEN
    ALTER TABLE qrtz_triggers ADD COLUMN preferred_node_auto bool not null default false;
  END IF;
END $$;

-- ==== 3.20 index alignment ====

-- === Drop the indexes whose columns changed but whose name did not ============
-- These have to go first: CREATE INDEX IF NOT EXISTS below would find the name
-- already taken and silently keep the old, wrong column order.

DROP INDEX IF EXISTS idx_qrtz_j_req_recovery;

DROP INDEX IF EXISTS idx_qrtz_t_next_fire_time;

DROP INDEX IF EXISTS idx_qrtz_t_nft_st;

-- === Create the indexes this version expects ===================================

CREATE INDEX IF NOT EXISTS idx_qrtz_j_req_recovery ON qrtz_job_details (sched_name, requests_recovery);

CREATE INDEX IF NOT EXISTS idx_qrtz_j_g_n ON qrtz_job_details (sched_name, job_group, job_name);

CREATE INDEX IF NOT EXISTS idx_qrtz_t_j ON qrtz_triggers (sched_name, job_name, job_group);

CREATE INDEX IF NOT EXISTS idx_qrtz_t_c ON qrtz_triggers (sched_name, calendar_name);

CREATE INDEX IF NOT EXISTS idx_qrtz_t_g_n ON qrtz_triggers (sched_name, trigger_group, trigger_name);

CREATE INDEX IF NOT EXISTS idx_qrtz_t_next_fire_time ON qrtz_triggers (sched_name, next_fire_time);

CREATE INDEX IF NOT EXISTS idx_qrtz_t_nft_st ON qrtz_triggers (sched_name, trigger_state, next_fire_time);

CREATE INDEX IF NOT EXISTS idx_qrtz_ft_inst_job_req_rcvry ON qrtz_fired_triggers (sched_name, instance_name, requests_recovery);

CREATE INDEX IF NOT EXISTS idx_qrtz_ft_j_g ON qrtz_fired_triggers (sched_name, job_name, job_group);

CREATE INDEX IF NOT EXISTS idx_qrtz_ft_t_g ON qrtz_fired_triggers (sched_name, trigger_name, trigger_group);

-- === Drop the ones it no longer uses ==========================================
-- Guarded, so each is a no-op when that index is not present.

DROP INDEX IF EXISTS idx_qrtz_j_grp;

DROP INDEX IF EXISTS idx_qrtz_t_g_j;

DROP INDEX IF EXISTS idx_qrtz_t_jg;

DROP INDEX IF EXISTS idx_qrtz_t_g;

DROP INDEX IF EXISTS idx_qrtz_t_state;

DROP INDEX IF EXISTS idx_qrtz_t_n_state;

DROP INDEX IF EXISTS idx_qrtz_t_n_g_state;

DROP INDEX IF EXISTS idx_qrtz_t_nft_misfire;

DROP INDEX IF EXISTS idx_qrtz_t_nft_st_misfire_grp;

DROP INDEX IF EXISTS idx_qrtz_t_nft_st_misfire;

DROP INDEX IF EXISTS idx_qrtz_ft_g_j;

DROP INDEX IF EXISTS idx_qrtz_ft_g_t;

DROP INDEX IF EXISTS idx_qrtz_ft_jg;

DROP INDEX IF EXISTS idx_qrtz_ft_tg;

DROP INDEX IF EXISTS idx_qrtz_ft_trig_inst_name;

DROP INDEX IF EXISTS idx_qrtz_ft_trig_nm_gp;

DROP INDEX IF EXISTS idx_qrtz_ft_trig_name;

DROP INDEX IF EXISTS idx_qrtz_ft_trig_group;

DROP INDEX IF EXISTS idx_qrtz_ft_job_name;

DROP INDEX IF EXISTS idx_qrtz_ft_job_group;

DROP INDEX IF EXISTS idx_qrtz_ft_job_req_recovery;
