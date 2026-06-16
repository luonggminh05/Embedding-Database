import hashlib
import logging
import os
import time
from urllib.parse import urlparse

import httpx
from langchain_community.document_loaders import (
    CSVLoader,
    PyPDFLoader,
    TextLoader,
    UnstructuredExcelLoader,
    UnstructuredPowerPointLoader,
    UnstructuredWordDocumentLoader,
)
from langchain_text_splitters import RecursiveCharacterTextSplitter
from pydantic_settings import BaseSettings
from watchdog.events import FileSystemEventHandler
from watchdog.observers.polling import PollingObserver as Observer

logging.basicConfig(level=logging.INFO, format="%(asctime)s - %(name)s - %(levelname)s - %(message)s")
logger = logging.getLogger(__name__)


class Settings(BaseSettings):
    API_URL: str = "http://localhost:8080/db/add"
    PAPERS_DIR_NAME: str = "papers"

    class Config:
        env_file = ".env"


settings = Settings()
PAPERS_DIR = os.path.join(os.path.dirname(__file__), settings.PAPERS_DIR_NAME)


def get_api_health_url() -> str:
    parsed = urlparse(settings.API_URL)
    return f"{parsed.scheme}://{parsed.netloc}/"


def wait_for_api(max_attempts: int = 30, delay_seconds: int = 5) -> bool:
    health_url = get_api_health_url()
    for attempt in range(1, max_attempts + 1):
        try:
            response = httpx.get(health_url, timeout=10.0)
            if response.status_code < 500:
                logger.info("API server is ready: %s", health_url)
                return True
        except Exception as exc:
            logger.warning("API not ready yet (%s/%s): %s", attempt, max_attempts, exc)
        time.sleep(delay_seconds)

    logger.error("API server did not become ready after %s attempts.", max_attempts)
    return False


def load_document(file_path: str):
    file_name = os.path.basename(file_path).lower()
    try:
        if file_name.endswith(".pdf"):
            return PyPDFLoader(file_path).load()
        if file_name.endswith(".docx"):
            return UnstructuredWordDocumentLoader(file_path).load()
        if file_name.endswith((".xlsx", ".xls")):
            return UnstructuredExcelLoader(file_path).load()
        if file_name.endswith(".pptx"):
            return UnstructuredPowerPointLoader(file_path).load()
        if file_name.endswith(".csv"):
            return CSVLoader(file_path).load()
        if file_name.endswith(".txt"):
            return TextLoader(file_path, encoding="utf-8").load()

        logger.warning("Skipping unsupported file: %s", file_name)
        return []
    except Exception as exc:
        logger.error("Error loading %s: %s", file_name, exc)
        return []


def process_and_ingest(file_path: str):
    logger.info("Detected file: %s", file_path)
    docs = load_document(file_path)
    if not docs:
        return

    logger.info("Loaded %s pages/chunks. Splitting into chunks...", len(docs))
    text_splitter = RecursiveCharacterTextSplitter(chunk_size=600, chunk_overlap=100)
    splits = text_splitter.split_documents(docs)

    if not splits:
        logger.warning("No text found to ingest.")
        return

    logger.info("Created %s chunks. Sending to API: %s", len(splits), settings.API_URL)
    documents = [chunk.page_content for chunk in splits]
    metadatas = [chunk.metadata for chunk in splits]

    file_name = os.path.basename(file_path).encode("utf-8")
    file_hash = hashlib.md5(file_name).hexdigest()
    ids = [f"{file_hash}_chunk_{i}" for i in range(len(splits))]

    payload = {
        "documents": documents,
        "metadatas": metadatas,
        "ids": ids,
    }

    try:
        response = httpx.post(settings.API_URL, json=payload, timeout=180.0)
        if response.status_code == 200:
            logger.info("Successfully ingested %s chunks.", len(documents))
        else:
            logger.error("API error: %s", response.text)
    except Exception as exc:
        logger.error("Could not connect to API: %s", exc)


class IngestEventHandler(FileSystemEventHandler):
    def on_created(self, event):
        if not event.is_directory:
            time.sleep(1)
            process_and_ingest(event.src_path)


def start_watcher():
    if not os.path.exists(PAPERS_DIR):
        os.makedirs(PAPERS_DIR)

    wait_for_api()
    logger.info("Watching folder: %s", PAPERS_DIR)

    for filename in os.listdir(PAPERS_DIR):
        filepath = os.path.join(PAPERS_DIR, filename)
        if os.path.isfile(filepath):
            logger.info("Found existing file: %s", filename)
            process_and_ingest(filepath)

    event_handler = IngestEventHandler()
    observer = Observer()
    observer.schedule(event_handler, PAPERS_DIR, recursive=False)
    observer.start()
    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        observer.stop()
    observer.join()


if __name__ == "__main__":
    start_watcher()
