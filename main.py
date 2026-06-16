import os
import json
import time
import logging
import asyncio
from collections import OrderedDict
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel
from typing import List, Dict, Any, Optional
from pydantic_settings import BaseSettings
from contextlib import asynccontextmanager
import httpx
import aioodbc

# Setup logging
logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(name)s - %(levelname)s - %(message)s')
logger = logging.getLogger(__name__)

class Settings(BaseSettings):
    TEI_URL: str = "http://localhost:8080"
    SQL_SERVER: str = "localhost"
    SQL_USER: str = "sa"
    SQL_PASSWORD: str = "YourStrong!Passw0rd"
    SQL_DATABASE: str = "master"
    QUERY_EMBED_CACHE_SIZE: int = 1024
    QUERY_EMBED_CACHE_TTL_SECONDS: int = 3600

    class Config:
        env_file = ".env"

settings = Settings()

# Globals for connection pooling
db_pool: aioodbc.Pool = None
http_client: httpx.AsyncClient = None
query_embedding_cache: OrderedDict[str, tuple[float, List[float]]] = OrderedDict()
query_embedding_inflight: Dict[str, asyncio.Task[List[List[float]]]] = {}
query_embedding_cache_lock = asyncio.Lock()

def normalize_cache_key(text: str) -> str:
    return " ".join(text.strip().lower().split())

async def get_query_embedding_cached(query: str) -> tuple[List[float], bool]:
    if settings.QUERY_EMBED_CACHE_SIZE <= 0:
        return (await get_embeddings_from_tei_async([query]))[0], False

    cache_key = normalize_cache_key(query)
    now = time.monotonic()
    created_task = False

    async with query_embedding_cache_lock:
        cached = query_embedding_cache.get(cache_key)
        if cached:
            expires_at, embedding = cached
            if expires_at > now:
                query_embedding_cache.move_to_end(cache_key)
                return embedding, True
            query_embedding_cache.pop(cache_key, None)

        task = query_embedding_inflight.get(cache_key)
        if task is None:
            task = asyncio.create_task(get_embeddings_from_tei_async([query]))
            query_embedding_inflight[cache_key] = task
            created_task = True

    try:
        embeddings = await task
        embedding = embeddings[0]
    except Exception:
        if created_task:
            async with query_embedding_cache_lock:
                query_embedding_inflight.pop(cache_key, None)
        raise

    if created_task:
        expires_at = time.monotonic() + max(1, settings.QUERY_EMBED_CACHE_TTL_SECONDS)
        async with query_embedding_cache_lock:
            query_embedding_cache[cache_key] = (expires_at, embedding)
            query_embedding_cache.move_to_end(cache_key)
            query_embedding_inflight.pop(cache_key, None)
            while len(query_embedding_cache) > settings.QUERY_EMBED_CACHE_SIZE:
                query_embedding_cache.popitem(last=False)

    return embedding, False

def get_odbc_dsn():
    return (
        "Driver={ODBC Driver 18 for SQL Server};"
        f"Server={settings.SQL_SERVER};"
        f"Database={settings.SQL_DATABASE};"
        f"UID={settings.SQL_USER};"
        f"PWD={settings.SQL_PASSWORD};"
        "TrustServerCertificate=yes;"
    )

async def _run_ddl(pool: aioodbc.Pool, sql: str):
    """Run a single DDL statement with autocommit=True in its own connection."""
    async with pool.acquire() as conn:
        conn.autocommit = True
        async with conn.cursor() as cursor:
            await cursor.execute(sql)

async def _fetchone(pool: aioodbc.Pool, sql: str):
    """Run a SELECT and return first row."""
    async with pool.acquire() as conn:
        conn.autocommit = True
        async with conn.cursor() as cursor:
            await cursor.execute(sql)
            return await cursor.fetchone()

async def _has_active_fulltext_index(pool: aioodbc.Pool) -> bool:
    row = await _fetchone(
        pool,
        """
        SELECT CASE
            WHEN EXISTS (
                SELECT 1
                FROM sys.fulltext_indexes
                WHERE object_id = OBJECT_ID('Documents')
            )
            THEN 1 ELSE 0
        END
        """,
    )
    return bool(row and row[0] == 1)

async def init_db(pool: aioodbc.Pool):
    retries = 10
    while retries > 0:
        try:
            # ── Step 1: Create Documents table ──────────────────────────────
            row = await _fetchone(pool, "SELECT * FROM sysobjects WHERE name='Documents' AND xtype='U'")
            if not row:
                await _run_ddl(pool, """
                    CREATE TABLE Documents (
                        id       VARCHAR(255)  CONSTRAINT PK_Documents PRIMARY KEY,
                        document NVARCHAR(MAX),
                        metadata NVARCHAR(MAX),
                        embedding VECTOR(1024)
                    );
                """)
                logger.info("Created Documents table.")

            # ── Step 2: Create Full-Text Catalog (own connection, committed) ─
            try:
                row = await _fetchone(pool, "SELECT * FROM sys.fulltext_catalogs WHERE name = 'ftCatalog'")
                if not row:
                    await _run_ddl(pool, "CREATE FULLTEXT CATALOG ftCatalog AS DEFAULT;")
                    logger.info("Created Full-Text Catalog.")
                    await asyncio.sleep(2)

            # ── Step 3: Create Full-Text Index (own connection) ──────────────
                row = await _fetchone(pool, "SELECT * FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID('Documents')")
                if not row:
                    await _run_ddl(pool, """
                        CREATE FULLTEXT INDEX ON Documents(document)
                        KEY INDEX PK_Documents ON ftCatalog
                        WITH CHANGE_TRACKING AUTO;
                    """)
                    logger.info("Created Full-Text Index.")
                    await asyncio.sleep(2)
            except Exception as e:
                logger.warning(f"Full-Text Index not ready; search will use vector-only fallback: {e}")

            # ── Step 4: Enable Preview Features for Vector support ───────────
            try:
                await _run_ddl(pool, "ALTER DATABASE SCOPED CONFIGURATION SET PREVIEW_FEATURES = ON;")
            except Exception as e:
                logger.warning(f"Could not set PREVIEW_FEATURES: {e}")

            # ── Step 5: Create DiskANN Vector Index (optional) ───────────────
            row = await _fetchone(pool, "SELECT * FROM sys.indexes WHERE name = 'idx_documents_embedding'")
            if not row:
                try:
                    await _run_ddl(pool, """
                        CREATE VECTOR INDEX idx_documents_embedding
                        ON Documents(embedding)
                        WITH (METRIC = 'cosine', TYPE = 'diskann');
                    """)
                    logger.info("Created DiskANN Vector Index.")
                except Exception as e:
                    logger.warning(f"Vector Index not created (fallback to exact search): {e}")

            logger.info("DB initialized successfully.")
            return
        except Exception as e:
            logger.warning(f"Waiting for SQL Server... {e}")
            retries -= 1
            await asyncio.sleep(5)
    logger.error("Could not initialize DB after retries.")

@asynccontextmanager
async def lifespan(app: FastAPI):
    global db_pool, http_client
    # Khởi tạo Global Pool với Retry
    dsn = get_odbc_dsn()
    retries = 5
    while retries > 0:
        try:
            db_pool = await aioodbc.create_pool(dsn=dsn, minsize=2, maxsize=10, autocommit=True)
            break
        except Exception as e:
            logger.warning(f"Waiting for SQL Server pool connection... {e}")
            retries -= 1
            await asyncio.sleep(5)
            
    if not db_pool:
        raise Exception("Failed to initialize database pool.")
    # Khởi tạo HTTP Client async
    http_client = httpx.AsyncClient(timeout=30.0)
    
    # Init DB schema
    await init_db(db_pool)
    
    yield
    
    # Cleanup on shutdown
    db_pool.close()
    await db_pool.wait_closed()
    await http_client.aclose()

app = FastAPI(title="Embedding & SQL Server Vector Service", lifespan=lifespan)

async def get_embeddings_from_tei_async(texts: List[str]) -> List[List[float]]:
    try:
        all_embeddings = []
        batch_size = 32
        for i in range(0, len(texts), batch_size):
            batch = texts[i:i+batch_size]
            response = await http_client.post(
                f"{settings.TEI_URL}/embed",
                json={"inputs": batch, "normalize": True, "truncate": True}
            )
            response.raise_for_status()
            all_embeddings.extend(response.json())
        return all_embeddings
    except Exception as e:
        logger.error(f"Error fetching embeddings from TEI: {e}")
        raise

class EmbedRequest(BaseModel):
    texts: List[str]

class AddDocumentsRequest(BaseModel):
    documents: List[str]
    metadatas: Optional[List[Dict[str, Any]]] = None
    ids: List[str]

class SearchRequest(BaseModel):
    query: str
    top_k: int = 5

@app.get("/")
async def health_check():
    return {"status": "ok", "message": "Async Embedding & SQL Server Service is running"}

@app.post("/embed")
async def embed_texts(req: EmbedRequest):
    try:
        embeddings = await get_embeddings_from_tei_async(req.texts)
        return {"embeddings": embeddings}
    except Exception as e:
        logger.error(f"Error in /embed: {e}")
        raise HTTPException(status_code=500, detail=str(e))

@app.post("/db/add")
async def add_to_db(req: AddDocumentsRequest):
    try:
        embeddings = await get_embeddings_from_tei_async(req.documents)
        
        async with db_pool.acquire() as conn:
            async with conn.cursor() as cursor:
                for i in range(len(req.documents)):
                    doc_id = req.ids[i]
                    doc_text = req.documents[i]
                    meta = json.dumps(req.metadatas[i]) if req.metadatas and req.metadatas[i] else "{}"
                    emb_json = json.dumps(embeddings[i])
                    
                    await cursor.execute("""
                        MERGE Documents AS target
                        USING (SELECT ? AS id, ? AS document, ? AS metadata, CAST(CAST(? AS VARCHAR(MAX)) AS VECTOR(1024)) AS embedding) AS source
                        ON (target.id = source.id)
                        WHEN MATCHED THEN 
                            UPDATE SET document = source.document, metadata = source.metadata, embedding = source.embedding
                        WHEN NOT MATCHED THEN
                            INSERT (id, document, metadata, embedding)
                            VALUES (source.id, source.document, source.metadata, source.embedding);
                    """, (doc_id, doc_text, meta, emb_json))
        
        logger.info(f"Upserted {len(req.documents)} chunks to SQL Server.")
        return {"status": "success", "count": len(req.documents)}
    except Exception as e:
        logger.error(f"Error in /db/add: {e}")
        raise HTTPException(status_code=500, detail=str(e))

@app.post("/db/search")
async def search_db(req: SearchRequest):
    try:
        start_time = time.perf_counter()
        
        # Gọi TEI
        tei_start = time.perf_counter()
        query_embedding, cache_hit = await get_query_embedding_cached(req.query)
        tei_time = time.perf_counter() - tei_start
        
        query_emb_json = json.dumps(query_embedding)
        top_k = max(1, min(int(req.top_k), 50))
        fts_ready = await _has_active_fulltext_index(db_pool)

        if fts_ready:
            sql = f"""
            WITH VectorBase AS (
                SELECT
                    id,
                    metadata,
                    VECTOR_DISTANCE('cosine', embedding, CAST(CAST(? AS VARCHAR(MAX)) AS VECTOR(1024))) AS distance
                FROM Documents
            ),
            VectorSearch AS (
                SELECT TOP (200)
                    id,
                    metadata,
                    distance,
                    ROW_NUMBER() OVER (ORDER BY distance ASC) AS vector_rank
                FROM VectorBase
                ORDER BY distance ASC
            ),
            KeywordSearch AS (
                SELECT [KEY] AS id,
                       ROW_NUMBER() OVER (ORDER BY [RANK] DESC) AS keyword_rank
                FROM FREETEXTTABLE(Documents, document, ?)
            ),
            RankedScores AS (
                SELECT v.id, v.metadata, v.distance,
                       v.vector_rank,
                       k.keyword_rank
                FROM VectorSearch v
                LEFT JOIN KeywordSearch k ON v.id = k.id
            )
            SELECT TOP ({top_k})
                id, metadata, distance,
                (1.0 / (60.0 + vector_rank)) + (1.0 / (60.0 + ISNULL(keyword_rank, 9999))) AS RRF_Score
            FROM RankedScores
            ORDER BY RRF_Score DESC
            """
            params = (query_emb_json, req.query)
        else:
            logger.warning("Full-Text Index is not active; using vector-only search.")
            sql = f"""
            WITH VectorBase AS (
                SELECT
                    id,
                    metadata,
                    VECTOR_DISTANCE('cosine', embedding, CAST(CAST(? AS VARCHAR(MAX)) AS VECTOR(1024))) AS distance
                FROM Documents
            ),
            VectorSearch AS (
                SELECT TOP ({top_k})
                    id,
                    metadata,
                    distance,
                    ROW_NUMBER() OVER (ORDER BY distance ASC) AS vector_rank
                FROM VectorBase
                ORDER BY distance ASC
            )
            SELECT
                id,
                metadata,
                distance,
                (1.0 / (60.0 + vector_rank)) AS RRF_Score
            FROM VectorSearch
            ORDER BY vector_rank ASC
            """
            params = (query_emb_json,)
        
        sql_start = time.perf_counter()
        async with db_pool.acquire() as conn:
            async with conn.cursor() as cursor:
                await cursor.execute(sql, params)
                rows = await cursor.fetchall()
                
                # Manual map to dict
                columns = [column[0] for column in cursor.description]
                dict_rows = [dict(zip(columns, row)) for row in rows]
        sql_time = time.perf_counter() - sql_start
        
        ids = []
        metadatas = []
        distances = []
        
        for row in dict_rows:
            ids.append(row['id'])
            metadatas.append(json.loads(row['metadata']))
            distances.append(row['distance'])
            
        total_time = time.perf_counter() - start_time
        logger.info(
            f"Search Performance - TEI: {tei_time:.3f}s | SQL: {sql_time:.3f}s | "
            f"Total: {total_time:.3f}s | CacheHit: {cache_hit}"
        )
        
        results = {
            "ids": [ids],
            "metadatas": [metadatas],
            "distances": [distances]
        }
        return results
    except Exception as e:
        logger.error(f"Error in /db/search: {e}")
        raise HTTPException(status_code=500, detail=str(e))

if __name__ == "__main__":
    import uvicorn
    api_port = int(os.environ.get("API_PORT", 8000))
    uvicorn.run("main:app", host="0.0.0.0", port=api_port, reload=True)
