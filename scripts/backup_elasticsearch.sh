#!/bin/bash

# Bash скрипт для создания бэкапа Elasticsearch индекса

BACKUP_PATH="${1:-./elasticsearch_backup}"
ELASTICSEARCH_URL="${2:-http://localhost:9200}"
USERNAME="${3:-elastic}"
PASSWORD="${4:-muVmg+YxSgExd2NKBttV}"

echo "Создание бэкапа Elasticsearch индекса..."

# Создаем директорию для бэкапа если её нет
mkdir -p "$BACKUP_PATH"
echo "Используется директория: $BACKUP_PATH"

# Проверяем подключение к Elasticsearch
echo "Проверка подключения к Elasticsearch..."
HEALTH_RESPONSE=$(curl -s -u "$USERNAME:$PASSWORD" "$ELASTICSEARCH_URL/_cluster/health")

if [ $? -ne 0 ]; then
    echo "Ошибка: Не удалось подключиться к Elasticsearch"
    exit 1
fi

CLUSTER_STATUS=$(echo "$HEALTH_RESPONSE" | jq -r '.status')
echo "Статус кластера: $CLUSTER_STATUS"

# Получаем список индексов
echo "Получение списка индексов..."
INDICES=$(curl -s -u "$USERNAME:$PASSWORD" "$ELASTICSEARCH_URL/_cat/indices?format=json" | jq -r '.[] | select(.index | startswith(".") | not) | .index')

for INDEX_NAME in $INDICES; do
    echo "Экспорт индекса: $INDEX_NAME"
    
    # Экспортируем маппинги индекса
    curl -s -u "$USERNAME:$PASSWORD" "$ELASTICSEARCH_URL/$INDEX_NAME/_mapping" | jq '.' > "$BACKUP_PATH/${INDEX_NAME}_mapping.json"
    
    # Экспортируем настройки индекса
    curl -s -u "$USERNAME:$PASSWORD" "$ELASTICSEARCH_URL/$INDEX_NAME/_settings" | jq '.' > "$BACKUP_PATH/${INDEX_NAME}_settings.json"
    
    # Экспортируем данные индекса с использованием scroll API
    SCROLL_RESPONSE=$(curl -s -u "$USERNAME:$PASSWORD" -H "Content-Type: application/json" \
        "$ELASTICSEARCH_URL/$INDEX_NAME/_search?scroll=5m" \
        -d '{"query": {"match_all": {}}, "size": 1000}')
    
    SCROLL_ID=$(echo "$SCROLL_RESPONSE" | jq -r '._scroll_id')
    ALL_DOCUMENTS=$(echo "$SCROLL_RESPONSE" | jq '.hits.hits')
    
    # Продолжаем скроллинг для получения всех документов
    while true; do
        SCROLL_RESPONSE=$(curl -s -u "$USERNAME:$PASSWORD" -H "Content-Type: application/json" \
            "$ELASTICSEARCH_URL/_search/scroll" \
            -d "{\"scroll\": \"5m\", \"scroll_id\": \"$SCROLL_ID\"}")
        
        HITS=$(echo "$SCROLL_RESPONSE" | jq '.hits.hits | length')
        
        if [ "$HITS" -eq 0 ]; then
            break
        fi
        
        NEW_DOCS=$(echo "$SCROLL_RESPONSE" | jq '.hits.hits')
        ALL_DOCUMENTS=$(echo "$ALL_DOCUMENTS $NEW_DOCS" | jq -s 'add')
        SCROLL_ID=$(echo "$SCROLL_RESPONSE" | jq -r '._scroll_id')
    done
    
    # Сохраняем документы
    echo "$ALL_DOCUMENTS" > "$BACKUP_PATH/${INDEX_NAME}_data.json"
    
    DOC_COUNT=$(echo "$ALL_DOCUMENTS" | jq 'length')
    echo "Экспортировано документов: $DOC_COUNT"
done

# Создаем метаданные бэкапа
TIMESTAMP=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
ES_VERSION=$(echo "$HEALTH_RESPONSE" | jq -r '.version.number // "unknown"')
CLUSTER_NAME=$(echo "$HEALTH_RESPONSE" | jq -r '.cluster_name')

cat > "$BACKUP_PATH/backup_metadata.json" << EOF
{
    "timestamp": "$TIMESTAMP",
    "elasticsearch_version": "$ES_VERSION",
    "cluster_name": "$CLUSTER_NAME",
    "indices": $(echo "$INDICES" | jq -R . | jq -s .)
}
EOF

echo ""
echo "Бэкап успешно создан в: $BACKUP_PATH"
echo "Файлы бэкапа:"
ls -la "$BACKUP_PATH"

echo ""
echo "Для восстановления используйте:"
echo "./scripts/restore_elasticsearch.sh $BACKUP_PATH"





