{
	admin off
	persist_config off
	auto_https off
}

(controller_proxy) {
	reverse_proxy https://kardia-controller-0.kardia-controller.kardia.svc.cluster.local:8444 https://kardia-controller-1.kardia-controller.kardia.svc.cluster.local:8444 {
		lb_policy first
		health_uri /healthz/leader
		health_interval 1s
		health_timeout 2s
		lb_try_duration 5s
		flush_interval -1
		transport http {
			tls_trust_pool file /certs/ca.crt
		}
	}
}

:8080 {
	header {
		Strict-Transport-Security "max-age=31536000; includeSubDomains"
		X-Content-Type-Options nosniff
		Referrer-Policy strict-origin-when-cross-origin
		Permissions-Policy "camera=(), microphone=(), geolocation=()"
		-Server
	}
	@notReport not path /api/v1/reports/*/content
	header @notReport {
		X-Frame-Options DENY
		Content-Security-Policy-Report-Only "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self'; worker-src 'self' blob:; connect-src 'self' https: wss:; frame-src 'self'; object-src 'none'; base-uri 'self'"
	}
	@ops path /metrics* /openapi* /swagger*
	handle @ops {
		respond 404
	}
	@controller {
		path /healthz/leader
	}
	@grpc {
		header Content-Type application/grpc*
	}
	handle @controller {
		import controller_proxy
	}
	handle @grpc {
		import controller_proxy
	}
	@api path /api /api/* /hubs /hubs/* /healthz /healthz/ready
	handle @api {
		reverse_proxy https://kardia-api.kardia.svc.cluster.local:8443 {
			transport http {
				tls_trust_pool file /certs/ca.crt
			}
		}
	}
	handle /assets/* {
		header Cache-Control no-store
		header Cache-Control "public, max-age=31536000, immutable" {
			match status 200 206 304
		}
		@current file {
			root /srv
			try_files {path}
		}
		handle @current {
			root * /srv
			file_server
		}
		handle {
			root * /opt/kardia/previous
			file_server
		}
	}
	handle {
		root * /srv
		route {
			try_files {path} /index.html
			header /index.html Cache-Control "no-cache, no-store"
			file_server
		}
	}
}
