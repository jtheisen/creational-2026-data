FROM python:3.12-slim
WORKDIR /app
COPY requirements.txt /app/requirements.txt
COPY workers/collection/requirements.txt /app/workers/collection/requirements.txt
RUN pip install --no-cache-dir -r /app/workers/collection/requirements.txt
COPY . /app
ENV PYTHONUNBUFFERED=1
ENTRYPOINT ["python", "-m", "workers.collection"]
