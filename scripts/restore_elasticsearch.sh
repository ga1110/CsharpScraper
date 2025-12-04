#!/bin/bash

# Bash скрипт для восстановления Elasticsearch индекса из бэкапа

BACKUP_PATH="${1:-./elasticsearch_backup}"
ELASTICSEARCH_URL="${2:-http://localhost:9200}"
USERNAME="${3:-elastic}"
PASSWORD="${4:-muVmg+YxSgExd2NKBttV}"
FORCE="${5:-false}"

echo "Восстановление Elasticsearch индекса из бэкапа..."

# Проверяем существование директории бэкапа
if [ ! -d "$BACKUP_PATH" ]; then
    echo "Ошибка: Директория бэкапа не найдена: $BACKUP_PATH"
    exit 1
fi

# Проверяем подключение к Elasticsearch
echo "Проверка подключения к Elasticsearch..."
HEALTH_RESPONSE=$(curl -s -u "$USERNAME:$PASSWORD" "$ELASTICSEARCH_URL/_cluster/health")

if [ $? -ne 0 ]; then
    echo "Ошибка: Не удалось подключиться к Elasticsearch"
    exit 1
fi

CLUSTER_STATUS=$(echo "$HEALTH_RESPONSE" | jq -r '.status')
echo "Статус кластера: $CLUSTER_STATUS"

# Читаем метаданные бэкапа
METADATA_FILE="$BACKUP_PATH/backup_metadata.json"
if [ -f "$METADATA_FILE" ]; then
    echo "Информация о бэкапе:"
    echo "  Дата создания: $(jq -r '.timestamp' "$METADATA_FILE")"
    echo "  Версия Elasticsearch: $(jq -r '.elasticsearch_version' "$METADATA_FILE")"
    echo ""
fi

# Получаем список файлов данных
DATA_FILES=$(find "$BACKUP_PATH" -name "*_data.json" -type f)

if [ -z "$DATA_FILES" ]; then
    echo "Ошибка: Файлы данных не найдены в директории бэкапа"
    exit 1
fi

for DATA_FILE in $DATA_FILES; do
    INDEX_NAME=$(basename "$DATA_FILE" "_data.json")
    
    echo "Восстановление индекса: $INDEX_NAME"
    
    # Проверяем, существует ли индекс
    INDEX_EXISTS=$(curl -s -o /dev/null -w "%{http_code}" -u "$USERNAME:$PASSWORD" "$ELASTICSEARCH_URL/$INDEX_NAME")
    
    if [ "$INDEX_EXISTS" = "200" ]; then
        if [ "$FORCE" != "true" ] && [ "$FORCE" != "--force" ]; then
            echo "Индекс '$INDEX_NAME' уже существует. Используйте --force для перезаписи."
            continue
        else
            echo "Удаление существующего индекса '$INDEX_NAME'..."
            curl -s -u "$USERNAME:$PASSWORD" -X DELETE "$ELASTICSEARCH_URL/$INDEX_NAME" > /dev/null
        fi
    fi
    
    # Подготавливаем тело запроса для создания индекса
    CREATE_BODY="{\"settings\": {}, \"mappings\": {}}"
    
    # Восстанавливаем настройки
    SETTINGS_FILE="$BACKUP_PATH/${INDEX_NAME}_settings.json"
    if [ -f "$SETTINGS_FILE" ]; then
        # Извлекаем только пользовательские настройки
        USER_SETTINGS=$(jq ".[\"$INDEX_NAME\"].settings.index | with_entries(select(.key | IN(\"creation_date\", \"uuid\", \"version\", \"provided_name\") | not))" "$SETTINGS_FILE")
        CREATE_BODY=$(echo "$CREATE_BODY" | jq --argjson settings "$USER_SETTINGS" '.settings = $settings')
    fi
    
    # Восстанавливаем маппинги
    MAPPING_FILE="$BACKUP_PATH/${INDEX_NAME}_mapping.json"
    if [ -f "$MAPPING_FILE" ]; then
        MAPPINGS=$(jq ".[\"$INDEX_NAME\"].mappings" "$MAPPING_FILE")
        CREATE_BODY=$(echo "$CREATE_BODY" | jq --argjson mappings "$MAPPINGS" '.mappings = $mappings')
    fi
    
    # Создаем индекс
    echo "Создание индекса '$INDEX_NAME'..."
    curl -s -u "$USERNAME:$PASSWORD" -H "Content-Type: application/json" \
        -X PUT "$ELASTICSEARCH_URL/$INDEX_NAME" \
        -d "$CREATE_BODY" > /dev/null
    
    # Восстанавливаем данные
    echo "Загрузка данных в индекс '$INDEX_NAME'..."
    
    DOCUMENTS=$(jq -c '.[]' "$DATA_FILE")
    BULK_BODY=""
    
    while IFS= read -r doc; do
        DOC_ID=$(echo "$doc" | jq -r '._id')
        DOC_SOURCE=$(echo "$doc" | jq -c '._source')
        
        INDEX_ACTION="{\"index\":{\"_index\":\"$INDEX_NAME\",\"_id\":\"$DOC_ID\"}}"
        BULK_BODY="$BULK_BODY$INDEX_ACTION\n$DOC_SOURCE\n"
    done <<< "$DOCUMENTS"
    
    # Отправляем bulk запрос
    if [ -n "$BULK_BODY" ]; then
        BULK_RESPONSE=$(echo -e "$BULK_BODY" | curl -s -u "$USERNAME:$PASSWORD" \
            -H "Content-Type: application/x-ndjson" \
            -X POST "$ELASTICSEARCH_URL/_bulk" \
            --data-binary @-)
        
        ERRORS=$(echo "$BULK_RESPONSE" | jq '[.items[] | select(.index.error)] | length')
        TOTAL_DOCS=$(echo "$DOCUMENTS" | wc -l)
        SUCCESS_DOCS=$((TOTAL_DOCS - ERRORS))
        
        if [ "$ERRORS" -gt 0 ]; then
            echo "Предупреждение: $ERRORS документов не удалось загрузить"
        fi
        
        echo "Загружено документов: $SUCCESS_DOCS"
    fi
    
    echo "Индекс '$INDEX_NAME' восстановлен"
    echo ""
done

# Обновляем индексы
echo "Обновление индексов..."
curl -s -u "$USERNAME:$PASSWORD" -X POST "$ELASTICSEARCH_URL/_refresh" > /dev/null

echo "Восстановление завершено успешно!"








