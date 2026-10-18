FROM python:3.12-slim
WORKDIR /app
COPY requirements.txt /app/requirements.txt
COPY workers/collection/requirements.txt /app/workers/collection/requirements.txt
RUN pip install --no-cache-dir -r /app/workers/collection/requirements.txt
COPY workers /app/workers
COPY extract_lifeforms.py build_candidate_pool.py parse_candidate_pool.py transform_ott_taxonomy.py correlate_ott_wikipedia.py /app/
ENV PYTHONUNBUFFERED=1
ENTRYPOINT ["python", "-m", "workers.collection"]
