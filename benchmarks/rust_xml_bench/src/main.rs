use bzip2::read::BzDecoder;
use quick_xml::events::Event;
use quick_xml::Reader;
use sha2::{Digest, Sha256};
use std::env;
use std::error::Error;
use std::fs::File;
use std::io::BufReader;
use std::time::Instant;

fn main() -> Result<(), Box<dyn Error>> {
    let path = env::args().nth(1).ok_or("usage: rust-xml-bench FILE.xml.bz2")?;
    let compressed = File::open(path)?;
    let decoder = BzDecoder::new(compressed);
    let mut reader = Reader::from_reader(BufReader::with_capacity(1024 * 1024, decoder));
    reader.config_mut().trim_text(false);

    let started = Instant::now();
    let mut buf = Vec::with_capacity(64 * 1024);
    let mut path: Vec<Vec<u8>> = Vec::with_capacity(8);
    let mut in_page = false;
    let mut page_id = String::new();
    let mut title = String::new();
    let mut namespace = String::new();
    let mut digest = Sha256::new();
    let mut count: u64 = 0;

    loop {
        match reader.read_event_into(&mut buf)? {
            Event::Start(e) => {
                let name = e.local_name().as_ref().as_bytes().to_vec();
                if name == b"page" {
                    in_page = true;
                    page_id.clear();
                    title.clear();
                    namespace.clear();
                }
                path.push(name);
            }
            Event::Text(e) if in_page => {
                let tail = path.as_slice();
                if tail.len() >= 2 && tail[tail.len() - 2] == b"page" {
                    let target = match tail.last().map(Vec::as_slice) {
                        Some(b"id") => Some(&mut page_id),
                        Some(b"title") => Some(&mut title),
                        Some(b"ns") => Some(&mut namespace),
                        _ => None,
                    };
                    if let Some(target) = target {
                        let decoded = e.as_ref();
                        let value = quick_xml::escape::unescape(decoded)?;
                        target.push_str(&value);
                    }
                }
            }
            Event::End(e) => {
                if e.local_name().as_ref().as_bytes() == b"page" && in_page {
                    count += 1;
                    digest.update(page_id.as_bytes());
                    digest.update(b"\t");
                    digest.update(title.as_bytes());
                    digest.update(b"\t");
                    digest.update(namespace.as_bytes());
                    digest.update(b"\n");
                    if count % 200_000 == 0 {
                        println!("Rust quick-xml: {count} pages, {:.1}s", started.elapsed().as_secs_f64());
                    }
                    in_page = false;
                }
                path.pop();
            }
            Event::Eof => break,
            _ => {}
        }
        buf.clear();
    }

    let hash = format!("{:x}", digest.finalize());
    println!("RESULT Rust quick-xml count={count} metadata_sha256={hash} elapsed_seconds={:.3}", started.elapsed().as_secs_f64());
    Ok(())
}
