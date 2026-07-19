-- SE_ZyweFrakcje: stan świata (brain, SQLite)
PRAGMA journal_mode=WAL;

CREATE TABLE IF NOT EXISTS factions (
  tag TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  state TEXT NOT NULL DEFAULT 'spokoj',   -- spokoj / napiecie / wojna
  action_budget INTEGER NOT NULL DEFAULT 3
);

CREATE TABLE IF NOT EXISTS relations (
  a TEXT NOT NULL,
  b TEXT NOT NULL,                        -- 'PLAYER' albo tag frakcji
  value REAL NOT NULL DEFAULT 0,          -- -100..100
  cap REAL NOT NULL DEFAULT 100,          -- trwały sufit (świat mściwy)
  PRIMARY KEY (a, b)
);

CREATE TABLE IF NOT EXISTS events (       -- pamięć długoterminowa (kontekst LLM)
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  ts INTEGER NOT NULL,
  faction TEXT,
  type TEXT NOT NULL,
  weight INTEGER NOT NULL DEFAULT 0,      -- 0 zwykłe, 2 ciężkie (nie wygasa)
  summary TEXT NOT NULL                   -- jednozdaniowy opis PL do promptu
);

CREATE TABLE IF NOT EXISTS pending_actions (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  due_ts INTEGER NOT NULL,
  faction TEXT NOT NULL,
  kind TEXT NOT NULL,
  payload TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS contracts (
  contract_id TEXT PRIMARY KEY,           -- musi przetrwać restart świata
  faction TEXT NOT NULL,
  kind TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'open',
  payload TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS bridge_state (
  file TEXT PRIMARY KEY,
  line_offset INTEGER NOT NULL DEFAULT 0
);
