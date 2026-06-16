import argparse
import concurrent.futures
import random
import statistics
import sys
import threading
import time
from typing import Dict, List, Tuple

import requests


THREAD_LOCAL = threading.local()


QUESTION_PRESETS = {
    "phap_luat": [
        "Pháp luật là gì và có những đặc trưng nào?",
        "Quy phạm pháp luật gồm những bộ phận nào?",
        "Quan hệ pháp luật là gì?",
        "Chủ thể của quan hệ pháp luật bao gồm những ai?",
        "Năng lực pháp luật và năng lực hành vi khác nhau như thế nào?",
        "Vi phạm pháp luật là gì và có các dấu hiệu nào?",
        "Trách nhiệm pháp lý là gì?",
        "Các hình thức thực hiện pháp luật gồm những gì?",
        "Nhà nước pháp quyền xã hội chủ nghĩa Việt Nam có đặc điểm nào?",
        "Hệ thống pháp luật Việt Nam gồm những ngành luật nào?",
        "Hiến pháp có vai trò gì trong hệ thống pháp luật?",
        "Luật hành chính điều chỉnh những quan hệ nào?",
        "Luật hình sự quy định về vấn đề gì?",
        "Luật dân sự điều chỉnh những quan hệ nào?",
        "Quyền và nghĩa vụ cơ bản của công dân được quy định như thế nào?",
        "Khách thể của vi phạm pháp luật là gì?",
        "Mặt khách quan của vi phạm pháp luật gồm những yếu tố nào?",
        "Lỗi cố ý và lỗi vô ý khác nhau như thế nào?",
        "Phân biệt vi phạm hành chính và tội phạm",
        "Các kiểu nhà nước trong lịch sử gồm những kiểu nào?",
    ],
    "mang_may_tinh": [
        "Mạng máy tính là gì?",
        "Mô hình OSI gồm mấy tầng và chức năng từng tầng là gì?",
        "Mô hình TCP/IP khác OSI như thế nào?",
        "Tầng vật lý trong OSI có chức năng gì?",
        "Tầng liên kết dữ liệu đảm nhận nhiệm vụ gì?",
        "Địa chỉ MAC là gì?",
        "Địa chỉ IP là gì?",
        "Phân biệt IPv4 và IPv6",
        "Subnet mask dùng để làm gì?",
        "Cách tính subnet trong mạng máy tính",
        "Router và switch khác nhau như thế nào?",
        "Giao thức TCP và UDP khác nhau ở đâu?",
        "DNS có chức năng gì?",
        "DHCP hoạt động như thế nào?",
        "HTTP và HTTPS khác nhau như thế nào?",
        "ARP là gì và dùng để làm gì?",
        "ICMP được sử dụng trong trường hợp nào?",
        "Công dụng của NAT trong mạng máy tính",
        "Phân biệt LAN WAN MAN",
        "Các biện pháp bảo mật mạng cơ bản là gì?",
    ],
}


def send_request(url: str, query: str, top_k: int, timeout: float) -> Tuple[bool, int, float, str]:
    payload = {"query": query, "top_k": top_k}
    session = getattr(THREAD_LOCAL, "session", None)
    if session is None:
        session = requests.Session()
        adapter = requests.adapters.HTTPAdapter(pool_connections=1, pool_maxsize=1)
        session.mount("http://", adapter)
        session.mount("https://", adapter)
        THREAD_LOCAL.session = session

    start = time.perf_counter()
    try:
        response = session.post(url, json=payload, timeout=timeout)
        elapsed = time.perf_counter() - start
        ok = 200 <= response.status_code < 300
        error = "" if ok else response.text[:300]
        return ok, response.status_code, elapsed, error
    except requests.RequestException as exc:
        elapsed = time.perf_counter() - start
        return False, 0, elapsed, str(exc)


def percentile(values: List[float], pct: float) -> float:
    if not values:
        return 0.0
    ordered = sorted(values)
    index = int((len(ordered) - 1) * pct)
    return ordered[index]


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    parser = argparse.ArgumentParser(description="Load test POST /db/search")
    parser.add_argument("--url", default="http://192.168.18.129:30001/db/search", help="Search endpoint URL")
    parser.add_argument("--requests", type=int, default=10000, help="Total requests")
    parser.add_argument("--concurrency", type=int, default=50, help="Parallel workers")
    parser.add_argument("--query", default=None, help="Use one fixed search query instead of random preset questions")
    parser.add_argument("--queries-file", default=None, help="Read one search query per line from a UTF-8 text file")
    parser.add_argument(
        "--subject",
        choices=["all", "phap_luat", "mang_may_tinh"],
        default="all",
        help="Question preset group used when --query is not provided",
    )
    parser.add_argument("--top-k", type=int, default=5, help="top_k value")
    parser.add_argument("--timeout", type=float, default=30.0, help="Request timeout seconds")
    parser.add_argument("--progress-every", type=int, default=500, help="Progress interval")
    parser.add_argument("--selection", choices=["random", "cycle"], default="random", help="Query selection strategy")
    args = parser.parse_args()

    if args.queries_file:
        with open(args.queries_file, "r", encoding="utf-8") as query_file:
            queries = [line.strip() for line in query_file if line.strip()]
        if not queries:
            raise ValueError(f"No queries found in {args.queries_file}")
    elif args.query:
        queries = [args.query]
    elif args.subject == "all":
        queries = QUESTION_PRESETS["phap_luat"] + QUESTION_PRESETS["mang_may_tinh"]
    else:
        queries = QUESTION_PRESETS[args.subject]

    latencies: List[float] = []
    errors: Dict[str, int] = {}
    status_counts: Dict[int, int] = {}
    success_count = 0
    failure_count = 0
    started = time.perf_counter()

    print(
        f"Sending {args.requests} requests to {args.url} "
        f"with concurrency={args.concurrency}, timeout={args.timeout}s, questions={len(queries)}"
    )
    print("Sample questions:")
    for question in queries[:5]:
        print(f"- {question}")

    if args.selection == "cycle":
        selected_queries = [queries[index % len(queries)] for index in range(args.requests)]
    else:
        selected_queries = [random.choice(queries) for _ in range(args.requests)]

    with concurrent.futures.ThreadPoolExecutor(max_workers=args.concurrency) as executor:
        futures = [executor.submit(send_request, args.url, query, args.top_k, args.timeout) for query in selected_queries]

        for index, future in enumerate(concurrent.futures.as_completed(futures), start=1):
            ok, status_code, elapsed, error = future.result()
            latencies.append(elapsed)
            status_counts[status_code] = status_counts.get(status_code, 0) + 1

            if ok:
                success_count += 1
            else:
                failure_count += 1
                key = f"{status_code}: {error}" if status_code else error
                errors[key] = errors.get(key, 0) + 1

            if args.progress_every > 0 and index % args.progress_every == 0:
                total_elapsed = time.perf_counter() - started
                rps = index / total_elapsed if total_elapsed else 0
                print(f"Progress {index}/{args.requests} | ok={success_count} fail={failure_count} rps={rps:.2f}")

    total_elapsed = time.perf_counter() - started
    rps = args.requests / total_elapsed if total_elapsed else 0

    print("\nSummary")
    print(f"Total time: {total_elapsed:.2f}s")
    print(f"Throughput: {rps:.2f} requests/s")
    print(f"Success: {success_count}")
    print(f"Failure: {failure_count}")
    print(f"Status counts: {dict(sorted(status_counts.items()))}")

    if latencies:
        print(f"Latency avg: {statistics.mean(latencies):.3f}s")
        print(f"Latency min: {min(latencies):.3f}s")
        print(f"Latency p50: {percentile(latencies, 0.50):.3f}s")
        print(f"Latency p90: {percentile(latencies, 0.90):.3f}s")
        print(f"Latency p95: {percentile(latencies, 0.95):.3f}s")
        print(f"Latency p99: {percentile(latencies, 0.99):.3f}s")
        print(f"Latency max: {max(latencies):.3f}s")

    if errors:
        print("\nTop errors")
        for error, count in sorted(errors.items(), key=lambda item: item[1], reverse=True)[:10]:
            print(f"{count}x {error}")

    return 0 if failure_count == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
