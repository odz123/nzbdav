.PHONY: build run stop clean help

# Default target
.DEFAULT_GOAL := help

# Version can be overridden
VERSION ?= dev

help: ## Show this help message
	@echo "NzbDav Docker Build Targets"
	@echo ""
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) | sort | awk 'BEGIN {FS = ":.*?## "}; {printf "\033[36m%-15s\033[0m %s\n", $$1, $$2}'

build: ## Build the Docker image
	@echo "Building Docker image with version: $(VERSION)"
	docker build --build-arg NZBDAV_VERSION=$(VERSION) -t nzbdav:latest -t nzbdav:$(VERSION) .

build-no-cache: ## Build the Docker image without using cache
	@echo "Building Docker image (no cache) with version: $(VERSION)"
	docker build --no-cache --build-arg NZBDAV_VERSION=$(VERSION) -t nzbdav:latest -t nzbdav:$(VERSION) .

run: ## Run the application using docker-compose
	docker-compose up -d

run-attached: ## Run the application in foreground (attached mode)
	docker-compose up

stop: ## Stop the running containers
	docker-compose down

restart: stop run ## Restart the application

logs: ## Show logs from the running container
	docker-compose logs -f

shell: ## Open a shell in the running container
	docker-compose exec nzbdav /bin/bash

clean: ## Remove containers, images, and volumes
	docker-compose down -v
	docker rmi nzbdav:latest || true
	docker rmi nzbdav:$(VERSION) || true

prune: ## Remove all unused Docker resources
	docker system prune -af

inspect: ## Inspect the built image
	docker inspect nzbdav:latest

size: ## Show the size of the Docker image
	docker images nzbdav:latest

test-run: ## Quick test run with minimal config
	@mkdir -p ./config
	docker run --rm -it \
		-p 3000:3000 \
		-v $$(pwd)/config:/config \
		-e PUID=1000 \
		-e PGID=1000 \
		nzbdav:latest
