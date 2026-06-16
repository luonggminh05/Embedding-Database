import os
import time
import httpx
import hashlib
import logging
from urllib.parse import urlparse
from watchdog.observers.polling import PollingObserver as Observer
from watchdog.events import FileSystemEventHandler
from pydantic_settings import BaseSettings

from langchain_community.document_loaders import (
    PyPDFLoader,
    CSVLoader,
    TextLoader,
    UnstructuredWordDocumentLoader,
    UnstructuredExcelLoader,
    UnstructuredPowerPointLoader
)
from langchain_text_splitters import RecursiveCharacterTextSplitter

# Setup logging
logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(name)s - %(levelname)s - %(message)s')
logger = logging.getLogger(__name__)

class Settings(BaseSettings):
    API_URL: str = "http://localhost:8000/db/add"
    PAPERS_DIR_NAME: str = "papers"

    class Config:
        env_file = ".env"

settings = Settings()

# Calculate PAPERS_DIR based on the script location or use an absolute path if mounted
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
                logger.info(f"API server is ready: {health_url}")
                return True
        except Exception as e:
            logger.warning(f"API not ready yet ({attempt}/{max_attempts}): {e}")
        time.sleep(delay_seconds)
    logger.error(f"API server did not become ready after {max_attempts} attempts.")
    return False

def load_document(file_path: str):
    file_name = os.path.basename(file_path).lower()
    docs = []
    try:
        if file_name.endswith(".pdf"):
            docs = PyPDFLoader(file_path).load()
        elif file_name.endswith(".docx"):
            docs = UnstructuredWordDocumentLoader(file_path).load()
        elif file_name.endswith((".xlsx", ".xls")):
            docs = UnstructuredExcelLoader(file_path).load()
        elif file_name.endswith(".pptx"):
            docs = UnstructuredPowerPointLoader(file_path).load()
        elif file_name.endswith(".csv"):
            docs = CSVLoader(file_path).load()
        elif file_name.endswith(".txt"):
            docs = TextLoader(file_path, encoding="utf-8").load()
        else:
            logger.warning(f"Bỏ qua file không hỗ trợ: {file_name}")
            return []
    except Exception as e:
        logger.error(f"Lỗi khi load {file_name}: {e}")
    return docs

def process_and_ingest(file_path: str):
    logger.info(f"Phát hiện file mới: {file_path}")
    docs = load_document(file_path)
    if not docs:
        return
    
    logger.info(f"Đã đọc {len(docs)} pages/chunks từ file. Đang chia nhỏ...")
    text_splitter = RecursiveCharacterTextSplitter(chunk_size=600, chunk_overlap=100)
    splits = text_splitter.split_documents(docs)
    
    if not splits:
        logger.warning("Không có text để nạp.")
        return
        
    logger.info(f"Đã chia thành {len(splits)} chunks. Đang gửi đến API: {settings.API_URL}")
    
    documents = [chunk.page_content for chunk in splits]
    metadatas = [chunk.metadata for chunk in splits]
    
    file_name = os.path.basename(file_path).encode('utf-8')
    file_hash = hashlib.md5(file_name).hexdigest()
    ids = [f"{file_hash}_chunk_{i}" for i in range(len(splits))]
    
    payload = {
        "documents": documents,
        "metadatas": metadatas,
        "ids": ids
    }
    
    try:
        response = httpx.post(settings.API_URL, json=payload, timeout=180.0)
        if response.status_code == 200:
            logger.info(f" Nạp thành công {len(documents)} chunks vào hệ thống.")
        else:
            logger.error(f" Lỗi từ API: {response.text}")
    except Exception as e:
        logger.error(f" Không thể kết nối tới API: {e}")

class IngestEventHandler(FileSystemEventHandler):
    def on_created(self, event):
        if not event.is_directory:
            # Đợi 1s
            time.sleep(1)
            process_and_ingest(event.src_path)

def start_watcher():
    if not os.path.exists(PAPERS_DIR):
        os.makedirs(PAPERS_DIR)
        
    # Chờ API Server khoảng 5s
    wait_for_api()
        
    logger.info(f"Đang theo dõi thư mục: {PAPERS_DIR}")
    
    for filename in os.listdir(PAPERS_DIR):
        filepath = os.path.join(PAPERS_DIR, filename)
        if os.path.isfile(filepath):
            logger.info(f"Tìm thấy file có sẵn: {filename}")
            process_and_ingest(filepath)

    # Theo dõi file mới 
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
